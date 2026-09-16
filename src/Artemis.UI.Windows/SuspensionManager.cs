using System;
using System.Threading;
using System.Threading.Tasks;
using Artemis.Core.Services;
using DryIoc;
using Microsoft.Win32;
using Serilog;

namespace Artemis.UI.Windows;

public class SuspensionManager
{
    private readonly ILogger _logger;
    private readonly IDeviceService _deviceService;
    // Sleeping raises both a power and a session event
    private readonly SemaphoreSlim _suspensionSemaphore = new(1, 1);
    private int _requestCount;

    public SuspensionManager(IContainer container)
    {
        _logger = container.Resolve<ILogger>();
        _deviceService = container.Resolve<IDeviceService>();

        try
        {
            SystemEvents.PowerModeChanged += SystemEventsOnPowerModeChanged;
            SystemEvents.SessionSwitch += SystemEventsOnSessionSwitch;
        }
        catch (Exception e)
        {
            _logger.Warning(e, "Could not subscribe to system events");
        }
    }

    private void SystemEventsOnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
            RequestSuspension(true, e.Mode.ToString());
        else if (e.Mode == PowerModes.Resume)
            RequestSuspension(false, e.Mode.ToString());
    }

    private void SystemEventsOnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
            RequestSuspension(true, e.Reason.ToString());
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            RequestSuspension(false, e.Reason.ToString());
    }

    private void RequestSuspension(bool suspend, string reason)
    {
        int request = Interlocked.Increment(ref _requestCount);
        _logger.Information("{Action} requested ({Reason})", suspend ? "Suspend" : "Resume", reason);
        Task.Run(() => SetDeviceSuspension(suspend, request));
    }

    private async Task SetDeviceSuspension(bool suspend, int request)
    {
        try
        {
            if (suspend)
            {
                // Suspend instantly, system is going into sleep at any moment
                await _suspensionSemaphore.WaitAsync();
                try
                {
                    _deviceService.SuspendDeviceProviders();
                }
                finally
                {
                    _suspensionSemaphore.Release();
                }
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                await _suspensionSemaphore.WaitAsync();
                try
                {
                    // A newer request takes precedence
                    if (request != Volatile.Read(ref _requestCount))
                    {
                        _logger.Debug("Skipping resume, a newer suspension request was made");
                        return;
                    }

                    _deviceService.ResumeDeviceProviders();
                }
                finally
                {
                    _suspensionSemaphore.Release();
                }
            }
        }
        catch (Exception e)
        {
            _logger.Error(e, "An error occurred while setting provider suspension to {Suspension}", suspend);
        }
    }
}
