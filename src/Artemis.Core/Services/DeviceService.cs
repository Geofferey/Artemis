using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Artemis.Core.DeviceProviders;
using Artemis.Core.Providers;
using Artemis.Core.Services.Core;
using Artemis.Core.Services.Models;
using Artemis.Storage.Entities.Surface;
using Artemis.Storage.Repositories.Interfaces;
using RGB.NET.Core;
using Serilog;

namespace Artemis.Core.Services;

internal class DeviceService : IDeviceService
{
    private readonly ILogger _logger;
    private readonly IPluginManagementService _pluginManagementService;
    private readonly IDeviceRepository _deviceRepository;
    private readonly Lazy<IRenderService> _renderService;
    private readonly Func<List<ILayoutProvider>> _getLayoutProviders;
    private readonly DeviceProviderHealthMonitor _healthMonitor;

    private readonly object _devicesLock = new();
    private readonly List<ArtemisDevice> _enabledDevices = [];
    private readonly List<ArtemisDevice> _devices = [];
    private readonly Dictionary<DeviceProvider, (IRGBDeviceProvider Provider, EventHandler<DevicesChangedEventArgs> Handler)> _devicesChangedHandlers = [];
    private readonly List<DeviceProvider> _suspendedDeviceProviders = [];
    private readonly object _suspensionLock = new();

    private volatile IReadOnlyCollection<ArtemisDevice> _devicesSnapshot = [];
    private volatile IReadOnlyCollection<ArtemisDevice> _enabledDevicesSnapshot = [];
    private volatile IReadOnlyCollection<DeviceProvider> _suspendedDeviceProvidersSnapshot = [];

    public DeviceService(ILogger logger,
        IPluginManagementService pluginManagementService,
        IDeviceRepository deviceRepository,
        Lazy<IRenderService> renderService,
        Func<List<ILayoutProvider>> getLayoutProviders,
        ISettingsService settingsService)
    {
        _logger = logger;
        _pluginManagementService = pluginManagementService;
        _deviceRepository = deviceRepository;
        _renderService = renderService;
        _getLayoutProviders = getLayoutProviders;
        _healthMonitor = new DeviceProviderHealthMonitor(logger, this, pluginManagementService, renderService, settingsService);

        RenderScale.RenderScaleMultiplierChanged += RenderScaleOnRenderScaleMultiplierChanged;
        _pluginManagementService.PluginFeatureDisabled += PluginManagementServiceOnPluginFeatureDisabled;
        Utilities.ShutdownRequested += (_, _) => _healthMonitor.Dispose();
        Utilities.RestartRequested += (_, _) => _healthMonitor.Dispose();
    }

    public IReadOnlyCollection<DeviceProvider> SuspendedDeviceProviders => _suspendedDeviceProvidersSnapshot;
    public IReadOnlyCollection<ArtemisDevice> EnabledDevices => _enabledDevicesSnapshot;
    public IReadOnlyCollection<ArtemisDevice> Devices => _devicesSnapshot;

    internal bool IsSuspended { get; private set; }
    internal object SuspensionLock => _suspensionLock;

    /// <inheritdoc />
    public void IdentifyDevice(ArtemisDevice device)
    {
        BlinkDevice(device, 0);
    }

    /// <inheritdoc />
    public void AddDeviceProvider(DeviceProvider deviceProvider)
    {
        _logger.Verbose("[AddDeviceProvider] Adding {DeviceProvider}", deviceProvider.GetType().Name);
        IRGBDeviceProvider rgbDeviceProvider = deviceProvider.RgbDeviceProvider;

        try
        {
            // Can't see why this would happen, RgbService used to do this though
            RemoveDevicesOfProvider(deviceProvider, false);

            List<Exception> providerExceptions = [];

            void DeviceProviderOnException(object? sender, ExceptionEventArgs e)
            {
                if (e.IsCritical)
                    providerExceptions.Add(e.Exception);
                else
                    _logger.Warning(e.Exception, "Device provider {deviceProvider} threw non-critical exception", deviceProvider.GetType().Name);
            }

            _logger.Verbose("[AddDeviceProvider] Initializing device provider");
            rgbDeviceProvider.Exception += DeviceProviderOnException;
            try
            {
                rgbDeviceProvider.Initialize();
            }
            finally
            {
                rgbDeviceProvider.Exception -= DeviceProviderOnException;
            }

            _logger.Verbose("[AddDeviceProvider] Attaching devices of device provider");
            if (providerExceptions.Count == 1)
                throw new ArtemisPluginException("RGB.NET threw exception: " + providerExceptions.First().Message, providerExceptions.First());
            if (providerExceptions.Count > 1)
                throw new ArtemisPluginException("RGB.NET threw multiple exceptions", new AggregateException(providerExceptions));

            SubscribeToDevicesChanged(deviceProvider, rgbDeviceProvider);

            if (!rgbDeviceProvider.Devices.Any())
            {
                _logger.Warning("Device provider {deviceProvider} has no devices", deviceProvider.GetType().Name);
                return;
            }

            List<ArtemisDevice> addedDevices = rgbDeviceProvider.Devices.Select(rgbDevice => GetArtemisDevice(rgbDevice, deviceProvider)).ToList();
            lock (_devicesLock)
            {
                foreach (ArtemisDevice artemisDevice in addedDevices)
                {
                    _devices.Add(artemisDevice);
                    if (artemisDevice.IsEnabled)
                        _enabledDevices.Add(artemisDevice);
                }

                _devices.Sort((a, b) => a.ZIndex - b.ZIndex);
                _enabledDevices.Sort((a, b) => a.ZIndex - b.ZIndex);
                UpdateDeviceSnapshots();
            }

            foreach (ArtemisDevice artemisDevice in addedDevices)
                _logger.Debug("Device provider {deviceProvider} added {deviceName}", deviceProvider.GetType().Name, artemisDevice.RgbDevice.DeviceInfo.DeviceName);

            OnDeviceProviderAdded(new DeviceProviderEventArgs(deviceProvider, addedDevices));
            foreach (ArtemisDevice artemisDevice in addedDevices)
                OnDeviceAdded(new DeviceEventArgs(artemisDevice));

            UpdateLeds();
        }
        catch (Exception e)
        {
            _logger.Error(e, "Exception during device loading for device provider {deviceProvider}", deviceProvider.GetType().Name);
            throw;
        }
    }

    /// <inheritdoc />
    public void RemoveDeviceProvider(DeviceProvider deviceProvider)
    {
        _logger.Verbose("[RemoveDeviceProvider] Removing {DeviceProvider}", deviceProvider.GetType().Name);

        try
        {
            RemoveDevicesOfProvider(deviceProvider, true);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Exception during device removal for device provider {deviceProvider}", deviceProvider.GetType().Name);
            throw;
        }
    }

    /// <param name="leftHanded"></param>
    /// <inheritdoc />
    public void AutoArrangeDevices(bool leftHanded)
    {
        List<ArtemisDevice> devices = Devices.ToList();
        SurfaceArrangement surfaceArrangement = SurfaceArrangement.GetDefaultArrangement(leftHanded);
        surfaceArrangement.Arrange(devices);
        foreach (ArtemisDevice artemisDevice in devices)
            artemisDevice.ApplyDefaultCategories();

        SaveDevices();
    }

    /// <inheritdoc />
    public void LoadDeviceLayout(ArtemisDevice device)
    {
        ILayoutProvider? provider = _getLayoutProviders().FirstOrDefault(p => p.IsMatch(device));
        if (provider == null)
            _logger.Warning("Could not find a layout provider for type {LayoutType} of device {Device}", device.LayoutSelection.Type, device);

        ArtemisLayout? layout = provider?.GetDeviceLayout(device);
        if (layout != null && !layout.IsValid)
        {
            _logger.Warning("Got an invalid layout {Layout} from {LayoutProvider}", layout, provider!.GetType().FullName);
            layout = null;
        }

        try
        {
            if (layout == null)
                device.ApplyLayout(null, false, false);
            else
                provider?.ApplyLayout(device, layout);

            UpdateLeds();
        }
        catch (Exception e)
        {
            device.LayoutSelection.ErrorState = e.Message;
            _logger.Error(e, "Failed to apply device layout");
        }
    }

    /// <inheritdoc />
    public void EnableDevice(ArtemisDevice device)
    {
        lock (_devicesLock)
        {
            if (device.IsEnabled)
                return;

            _enabledDevices.Add(device);
            device.IsEnabled = true;
            UpdateDeviceSnapshots();
        }

        device.Save();
        _deviceRepository.Save(device.DeviceEntity);

        OnDeviceEnabled(new DeviceEventArgs(device));
        UpdateLeds();
    }

    /// <inheritdoc />
    public void DisableDevice(ArtemisDevice device)
    {
        lock (_devicesLock)
        {
            if (!device.IsEnabled)
                return;

            _enabledDevices.Remove(device);
            device.IsEnabled = false;
            UpdateDeviceSnapshots();
        }

        device.Save();
        _deviceRepository.Save(device.DeviceEntity);

        OnDeviceDisabled(new DeviceEventArgs(device));
        UpdateLeds();
    }

    /// <inheritdoc />
    public void SaveDevice(ArtemisDevice artemisDevice)
    {
        artemisDevice.Save();
        _deviceRepository.Save(artemisDevice.DeviceEntity);
        UpdateLeds();
    }

    /// <inheritdoc />
    public void SaveDevices()
    {
        IReadOnlyCollection<ArtemisDevice> devices = Devices;
        foreach (ArtemisDevice artemisDevice in devices)
            artemisDevice.Save();
        _deviceRepository.SaveRange(devices.Select(d => d.DeviceEntity));
        UpdateLeds();
    }

    /// <inheritdoc />
    public void SuspendDeviceProviders()
    {
        lock (_suspensionLock)
        {
            _logger.Information("Suspending all device providers");

            IsSuspended = true;
            bool wasPaused = _renderService.Value.IsPaused;
            try
            {
                _renderService.Value.IsPaused = true;
                foreach (DeviceProvider deviceProvider in _pluginManagementService.GetFeaturesOfType<DeviceProvider>().Where(d => d.SuspendSupported))
                    SuspendDeviceProvider(deviceProvider);
            }
            finally
            {
                _renderService.Value.IsPaused = wasPaused;
            }
        }
    }

    /// <inheritdoc />
    public void ResumeDeviceProviders()
    {
        lock (_suspensionLock)
        {
            _logger.Information("Resuming all device providers");

            bool wasPaused = _renderService.Value.IsPaused;
            try
            {
                _renderService.Value.IsPaused = true;
                foreach (DeviceProvider deviceProvider in _suspendedDeviceProviders.ToList())
                    ResumeDeviceProvider(deviceProvider);
            }
            finally
            {
                _renderService.Value.IsPaused = wasPaused;
                IsSuspended = false;
            }
        }

        _healthMonitor.ScheduleCheck(TimeSpan.FromSeconds(10), "resume");
    }

    private void SuspendDeviceProvider(DeviceProvider deviceProvider)
    {
        if (_suspendedDeviceProviders.Contains(deviceProvider))
        {
            _logger.Warning("Device provider {DeviceProvider} is already suspended", deviceProvider.Info.Name);
            return;
        }

        try
        {
            _pluginManagementService.DisablePluginFeature(deviceProvider, false);
        }
        catch (Exception e)
        {
            // Still mark it as suspended if it ended up disabled, otherwise it's never resumed
            if (deviceProvider.IsEnabled)
            {
                _logger.Error(e, "Device provider {DeviceProvider} failed to suspend", deviceProvider.Info.Name);
                return;
            }

            _logger.Warning(e, "Device provider {DeviceProvider} threw while being disabled for suspension", deviceProvider.Info.Name);
        }

        try
        {
            deviceProvider.Suspend();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "Device provider {DeviceProvider} threw while suspending", deviceProvider.Info.Name);
        }

        _suspendedDeviceProviders.Add(deviceProvider);
        _suspendedDeviceProvidersSnapshot = _suspendedDeviceProviders.ToList().AsReadOnly();
        _logger.Information("Device provider {DeviceProvider} suspended", deviceProvider.Info.Name);
    }

    private void ResumeDeviceProvider(DeviceProvider deviceProvider)
    {
        try
        {
            _pluginManagementService.EnablePluginFeature(deviceProvider, false, true);
            _suspendedDeviceProviders.Remove(deviceProvider);
            _suspendedDeviceProvidersSnapshot = _suspendedDeviceProviders.ToList().AsReadOnly();
            _logger.Information("Device provider {DeviceProvider} resumed", deviceProvider.Info.Name);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Device provider {DeviceProvider} failed to resume", deviceProvider.Info.Name);
        }
    }

    private void RemoveDevicesOfProvider(DeviceProvider deviceProvider, bool alwaysRaiseProviderRemoved)
    {
        UnsubscribeFromDevicesChanged(deviceProvider);

        List<ArtemisDevice> toRemove;
        lock (_devicesLock)
        {
            toRemove = _devices.Where(a => a.DeviceProvider.Id == deviceProvider.Id).ToList();
            foreach (ArtemisDevice device in toRemove)
            {
                _devices.Remove(device);
                _enabledDevices.Remove(device);
            }

            UpdateDeviceSnapshots();
        }

        if (!toRemove.Any() && !alwaysRaiseProviderRemoved)
            return;

        _logger.Verbose("[RemoveDeviceProvider] Removed {Count} device(s) of {DeviceProvider}", toRemove.Count, deviceProvider.GetType().Name);

        OnDeviceProviderRemoved(new DeviceProviderEventArgs(deviceProvider, toRemove));
        foreach (ArtemisDevice artemisDevice in toRemove)
            OnDeviceRemoved(new DeviceEventArgs(artemisDevice));

        UpdateLeds();
    }

    private void SubscribeToDevicesChanged(DeviceProvider deviceProvider, IRGBDeviceProvider rgbDeviceProvider)
    {
        void Handler(object? sender, DevicesChangedEventArgs e)
        {
            _logger.Information("Device provider {DeviceProvider} reported devices changed at runtime ({Action} {Device})",
                deviceProvider.GetType().Name, e.Action, e.Device.DeviceInfo.DeviceName);
        }

        UnsubscribeFromDevicesChanged(deviceProvider);
        lock (_devicesLock)
        {
            rgbDeviceProvider.DevicesChanged += Handler;
            _devicesChangedHandlers[deviceProvider] = (rgbDeviceProvider, Handler);
        }
    }

    private void UnsubscribeFromDevicesChanged(DeviceProvider deviceProvider)
    {
        lock (_devicesLock)
        {
            if (!_devicesChangedHandlers.Remove(deviceProvider, out (IRGBDeviceProvider Provider, EventHandler<DevicesChangedEventArgs> Handler) subscription))
                return;

            subscription.Provider.DevicesChanged -= subscription.Handler;
        }
    }

    private void UpdateDeviceSnapshots()
    {
        _devicesSnapshot = _devices.ToList().AsReadOnly();
        _enabledDevicesSnapshot = _enabledDevices.ToList().AsReadOnly();
    }

    private ArtemisDevice GetArtemisDevice(IRGBDevice rgbDevice, DeviceProvider deviceProvider)
    {
        string deviceIdentifier = rgbDevice.GetDeviceIdentifier();
        DeviceEntity? deviceEntity = _deviceRepository.Get(deviceIdentifier);

        ArtemisDevice device;
        if (deviceEntity != null)
            device = new ArtemisDevice(rgbDevice, deviceProvider, deviceEntity);
        // Fall back on creating a new device
        else
        {
            _logger.Information("No device config found for {DeviceInfo}, device hash: {DeviceHashCode}. Adding a new entry", rgbDevice.DeviceInfo, deviceIdentifier);
            device = new ArtemisDevice(rgbDevice, deviceProvider);
            _deviceRepository.Add(device.DeviceEntity);
        }

        LoadDeviceLayout(device);
        return device;
    }

    private void BlinkDevice(ArtemisDevice device, int blinkCount)
    {
        RGBSurface surface = _renderService.Value.Surface;

        // Create a LED group way at the top
        ListLedGroup ledGroup = new(surface, device.Leds.Select(l => l.RgbLed))
        {
            Brush = new SolidColorBrush(new Color(255, 255, 255)),
            ZIndex = 999
        };

        // After 200ms, detach the LED group
        Task.Run(async () =>
        {
            await Task.Delay(200);
            ledGroup.Detach();

            if (blinkCount < 5)
            {
                // After another 200ms, start over, repeat six times
                await Task.Delay(200);
                BlinkDevice(device, blinkCount + 1);
            }
        });
    }

    private void CalculateRenderProperties()
    {
        foreach (ArtemisDevice artemisDevice in Devices)
            artemisDevice.CalculateRenderProperties();
        UpdateLeds();
    }

    private void UpdateLeds()
    {
        OnLedsChanged();
    }

    private void RenderScaleOnRenderScaleMultiplierChanged(object? sender, EventArgs e)
    {
        CalculateRenderProperties();
    }

    private void PluginManagementServiceOnPluginFeatureDisabled(object? sender, PluginFeatureEventArgs e)
    {
        if (e.PluginFeature is not DeviceProvider deviceProvider)
            return;

        try
        {
            if (Devices.Any(d => d.DeviceProvider.Id == deviceProvider.Id))
            {
                _logger.Warning("Device provider {DeviceProvider} was disabled without removing its devices, removing them now", deviceProvider.GetType().Name);
                RemoveDevicesOfProvider(deviceProvider, true);
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to remove devices of disabled device provider {DeviceProvider}", deviceProvider.GetType().Name);
        }
    }

    #region Events

    /// <inheritdoc />
    public event EventHandler<DeviceEventArgs>? DeviceAdded;

    /// <inheritdoc />
    public event EventHandler<DeviceEventArgs>? DeviceRemoved;

    /// <inheritdoc />
    public event EventHandler<DeviceEventArgs>? DeviceEnabled;

    /// <inheritdoc />
    public event EventHandler<DeviceEventArgs>? DeviceDisabled;

    /// <inheritdoc />
    public event EventHandler<DeviceProviderEventArgs>? DeviceProviderAdded;

    /// <inheritdoc />
    public event EventHandler<DeviceProviderEventArgs>? DeviceProviderRemoved;

    /// <inheritdoc />
    public event EventHandler? LedsChanged;

    protected virtual void OnDeviceAdded(DeviceEventArgs e)
    {
        DeviceAdded?.Invoke(this, e);
    }

    protected virtual void OnDeviceRemoved(DeviceEventArgs e)
    {
        DeviceRemoved?.Invoke(this, e);
    }

    protected virtual void OnDeviceEnabled(DeviceEventArgs e)
    {
        DeviceEnabled?.Invoke(this, e);
    }

    protected virtual void OnDeviceDisabled(DeviceEventArgs e)
    {
        DeviceDisabled?.Invoke(this, e);
    }

    protected virtual void OnDeviceProviderAdded(DeviceProviderEventArgs e)
    {
        DeviceProviderAdded?.Invoke(this, e);
    }

    protected virtual void OnDeviceProviderRemoved(DeviceProviderEventArgs e)
    {
        DeviceProviderRemoved?.Invoke(this, e);
    }

    protected virtual void OnLedsChanged()
    {
        LedsChanged?.Invoke(this, EventArgs.Empty);
    }

    #endregion
}
