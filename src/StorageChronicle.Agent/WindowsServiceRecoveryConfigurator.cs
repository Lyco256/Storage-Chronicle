using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StorageChronicle.Agent;

/// <summary>Applies the three-stage Windows Service recovery policy after installation.</summary>
internal static class WindowsServiceRecoveryConfigurator
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const int ServiceConfigFailureActions = 2;
    private const int ServiceActionRestart = 2;
    private const string ServiceName = "StorageChronicleAgent";

    /// <summary>Configures restart delays of 5, 15, and 60 seconds when running under Windows.</summary>
    public static bool TryConfigure()
    {
        if (!OperatingSystem.IsWindows()) return false;

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == nint.Zero) return false;
        try
        {
            var service = OpenService(manager, ServiceName, ServiceChangeConfig);
            if (service == nint.Zero) return false;
            try
            {
                var actionSize = Marshal.SizeOf<ServiceAction>();
                var actionsPointer = Marshal.AllocHGlobal(actionSize * 3);
                try
                {
                    Marshal.StructureToPtr(new ServiceAction(ServiceActionRestart, 5_000), actionsPointer, false);
                    Marshal.StructureToPtr(new ServiceAction(ServiceActionRestart, 15_000), actionsPointer + actionSize, false);
                    Marshal.StructureToPtr(new ServiceAction(ServiceActionRestart, 60_000), actionsPointer + (actionSize * 2), false);
                    var actions = new ServiceFailureActions(86_400, nint.Zero, nint.Zero, 3, actionsPointer);
                    return ChangeServiceConfig2(service, ServiceConfigFailureActions, ref actions);
                }
                finally
                {
                    Marshal.FreeHGlobal(actionsPointer);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        catch (Win32Exception exception)
        {
            Debug.WriteLine($"Storage Chronicle service recovery configuration failed: {exception.NativeErrorCode}");
            return false;
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint serviceManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2(nint service, int infoLevel, ref ServiceFailureActions serviceConfig);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ServiceAction(int Type, uint Delay);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ServiceFailureActions(int ResetPeriod, nint RebootMessage, nint Command, uint ActionCount, nint Actions);
}
