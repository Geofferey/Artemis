using System;
using System.Reflection;
using System.Threading.Tasks;
using RGB.NET.Core;

namespace Artemis.Core.Services.Core;

// RGB.NET's DeviceUpdateTrigger doesn't catch exceptions in its update loop, so a device that disappears silently
// stops updating. Stop() is async void and awaits the faulted loop, crashing the application.
internal static class DeviceUpdateTriggerInspector
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly PropertyInfo? UpdateTaskProperty = typeof(DeviceUpdateTrigger).GetProperty("UpdateTask", Flags);
    private static readonly PropertyInfo? IsRunningProperty = typeof(DeviceUpdateTrigger).GetProperty("IsRunning", Flags);

    public static bool IsSupported => UpdateTaskProperty?.CanRead == true && UpdateTaskProperty.CanWrite && IsRunningProperty?.CanRead == true;

    public static bool TryGetFault(IDeviceUpdateTrigger trigger, out Exception? exception)
    {
        exception = null;
        if (!IsSupported || trigger is not DeviceUpdateTrigger deviceUpdateTrigger)
            return false;

        try
        {
            if (UpdateTaskProperty!.GetValue(deviceUpdateTrigger) is not Task updateTask)
                return false;

            if (updateTask.IsFaulted)
            {
                exception = updateTask.Exception?.GetBaseException();
                return true;
            }

            return updateTask.IsCompleted && IsRunningProperty!.GetValue(deviceUpdateTrigger) is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool Defuse(IDeviceUpdateTrigger trigger)
    {
        if (!TryGetFault(trigger, out _))
            return false;

        try
        {
            // Not a cached task because Stop disposes it
            UpdateTaskProperty!.SetValue(trigger, Task.FromResult(new object()));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
