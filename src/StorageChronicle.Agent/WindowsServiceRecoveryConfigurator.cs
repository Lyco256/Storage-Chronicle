using System.ComponentModel;
using System.Runtime.InteropServices;

namespace StorageChronicle.Agent;

/// <summary>Applies the required recovery policy only when explicitly invoked by the MSI.</summary>
internal static class WindowsServiceRecoveryConfigurator
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const int ServiceConfigFailureActions = 2;
    private const int ServiceActionRestart = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const uint ServiceWin32OwnProcess = 0x0010;
    private const string ServiceName = "StorageChronicleAgent";
    private const string ServiceAccount = "LocalSystem";

    /// <summary>Sets the three-stage recovery delays after confirming this executable owns the service.</summary>
    /// <returns><see langword="true"/> only when the verified Storage Chronicle service was configured.</returns>
    public static bool TryConfigureInstalledService()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.ProcessPath)) return false;
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == nint.Zero) return false;
        try
        {
            var service = OpenService(manager, ServiceName, ServiceQueryConfig | ServiceChangeConfig);
            if (service == nint.Zero) return false;
            try
            {
                if (!IsExpectedProductService(service, Environment.ProcessPath)) return false;
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
        catch (Win32Exception)
        {
            return false;
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static bool IsExpectedProductService(nint service, string executablePath)
    {
        _ = QueryServiceConfig(service, nint.Zero, 0, out var requiredBytes);
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || requiredBytes == 0) return false;
        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!QueryServiceConfig(service, buffer, requiredBytes, out _)) return false;
            var config = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            var serviceBinary = Marshal.PtrToStringUni(config.BinaryPathName);
            var serviceAccount = Marshal.PtrToStringUni(config.ServiceStartName);
            if (serviceBinary is null || !string.Equals(serviceAccount, ServiceAccount, StringComparison.OrdinalIgnoreCase) ||
                (config.ServiceType & ServiceWin32OwnProcess) == 0) return false;

            var registeredExecutable = GetExecutablePath(serviceBinary);
            return string.Equals(Path.GetFullPath(registeredExecutable), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetExecutablePath(string commandLine)
    {
        var trimmed = commandLine.TrimStart();
        if (trimmed.Length == 0) return string.Empty;
        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : string.Empty;
        }

        var separator = trimmed.IndexOfAny([' ', '\t']);
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint serviceManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(nint service, nint config, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(nint service, int infoLevel, ref ServiceFailureActions serviceConfig);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ServiceAction(int Type, uint Delay);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ServiceFailureActions(int ResetPeriod, nint RebootMessage, nint Command, uint ActionCount, nint Actions);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ServiceConfiguration(
        uint ServiceType,
        uint StartType,
        uint ErrorControl,
        nint BinaryPathName,
        nint LoadOrderGroup,
        uint TagId,
        nint Dependencies,
        nint ServiceStartName,
        nint DisplayName);
}
