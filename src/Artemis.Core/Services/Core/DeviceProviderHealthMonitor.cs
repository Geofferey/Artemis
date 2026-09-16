using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Artemis.Core.DeviceProviders;
using RGB.NET.Core;
using Serilog;

namespace Artemis.Core.Services.Core;

internal sealed class DeviceProviderHealthMonitor : IDisposable
{
    private const int MaxReloadAttempts = 5;
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthyResetTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] ReloadBackoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    private readonly ILogger _logger;
    private readonly DeviceService _deviceService;
    private readonly IPluginManagementService _pluginManagementService;
    private readonly Lazy<IRenderService> _renderService;
    private readonly PluginSetting<bool> _enabledSetting;
    private readonly Dictionary<DeviceProvider, ReloadState> _reloadStates = [];
    private readonly Timer _timer;

    private DateTime _lastTick = DateTime.UtcNow;
    private int _checking;
    private bool _disposed;

    public DeviceProviderHealthMonitor(ILogger logger,
        DeviceService deviceService,
        IPluginManagementService pluginManagementService,
        Lazy<IRenderService> renderService,
        ISettingsService settingsService)
    {
        _logger = logger;
        _deviceService = deviceService;
        _pluginManagementService = pluginManagementService;
        _renderService = renderService;
        _enabledSetting = settingsService.GetSetting("Core.DeviceProviderHealthMonitor.Enabled", true);

        if (!DeviceUpdateTriggerInspector.IsSupported)
            _logger.Warning("Device provider health monitoring is unavailable, the RGB.NET update trigger internals were not found");

        _timer = new Timer(TimerOnTick, null, FirstCheckDelay, CheckInterval);
    }

    public void ScheduleCheck(TimeSpan delay, string reason)
    {
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                Check(reason);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Scheduled device provider health check failed");
            }
        });
    }

    private void TimerOnTick(object? state)
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan sinceLastTick = now - _lastTick;
        _lastTick = now;

        // A large gap means the system was asleep, resuming schedules its own check
        if (sinceLastTick > CheckInterval * 3)
            return;

        Check("interval");
    }

    private void Check(string reason)
    {
        if (_disposed || !_enabledSetting.Value || !DeviceUpdateTriggerInspector.IsSupported)
            return;
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return;

        try
        {
            if (_deviceService.IsSuspended || _renderService.Value.IsPaused)
                return;

            foreach (DeviceProvider deviceProvider in _pluginManagementService.GetFeaturesOfType<DeviceProvider>().Where(d => d.IsEnabled))
                CheckDeviceProvider(deviceProvider, reason);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Device provider health check failed");
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private void CheckDeviceProvider(DeviceProvider deviceProvider, string reason)
    {
        Exception? fault = null;
        bool faulted = false;
        foreach ((int _, IDeviceUpdateTrigger trigger) in deviceProvider.RgbDeviceProvider.UpdateTriggers)
        {
            if (!DeviceUpdateTriggerInspector.TryGetFault(trigger, out Exception? triggerFault))
                continue;

            faulted = true;
            fault ??= triggerFault;
        }

        DateTime now = DateTime.UtcNow;
        if (!_reloadStates.TryGetValue(deviceProvider, out ReloadState? state))
        {
            state = new ReloadState();
            _reloadStates[deviceProvider] = state;
        }

        if (!faulted)
        {
            if (state.Attempts > 0 && now - state.LastReload > HealthyResetTime)
            {
                _logger.Information("Device provider {DeviceProvider} has been healthy since its last reload", deviceProvider.Info.Name);
                _reloadStates.Remove(deviceProvider);
            }

            return;
        }

        if (state.Attempts >= MaxReloadAttempts)
        {
            if (!state.GaveUp)
                _logger.Error(fault, "Device provider {DeviceProvider} update loop keeps dying, giving up after {Attempts} reloads", deviceProvider.Info.Name, state.Attempts);
            state.GaveUp = true;
            return;
        }

        if (now < state.NextAttempt)
            return;

        _logger.Warning(fault, "Device provider {DeviceProvider} update loop died, reloading ({Reason})", deviceProvider.Info.Name, reason);
        Reload(deviceProvider, state, now);
    }

    private void Reload(DeviceProvider deviceProvider, ReloadState state, DateTime now)
    {
        lock (_deviceService.SuspensionLock)
        {
            if (_disposed || _deviceService.IsSuspended || !deviceProvider.IsEnabled)
                return;

            state.Attempts++;
            state.LastReload = now;
            state.NextAttempt = now + ReloadBackoff[Math.Min(state.Attempts, ReloadBackoff.Length) - 1];

            try
            {
                _pluginManagementService.DisablePluginFeature(deviceProvider, false);
            }
            catch (Exception e)
            {
                _logger.Warning(e, "Device provider {DeviceProvider} threw while being disabled for a reload", deviceProvider.Info.Name);
            }

            try
            {
                _pluginManagementService.EnablePluginFeature(deviceProvider, false, true);
                _logger.Information("Reloaded device provider {DeviceProvider} (attempt {Attempt}/{MaxAttempts})", deviceProvider.Info.Name, state.Attempts, MaxReloadAttempts);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to reload device provider {DeviceProvider}", deviceProvider.Info.Name);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    private sealed class ReloadState
    {
        public int Attempts { get; set; }
        public DateTime LastReload { get; set; }
        public DateTime NextAttempt { get; set; }
        public bool GaveUp { get; set; }
    }
}
