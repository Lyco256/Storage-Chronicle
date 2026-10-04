using Microsoft.Win32;

namespace StorageChronicle.Agent;

/// <summary>Provides a stable, read-only Windows installation identity for PC-local media-consent binding.</summary>
internal static class WindowsPcIdentity
{
    private const string CryptographyRegistryPath = @"SOFTWARE\Microsoft\Cryptography";

    /// <summary>Reads and canonicalizes the Windows MachineGuid without changing the registry.</summary>
    /// <returns>A stable opaque identifier scoped to this Windows installation.</returns>
    /// <exception cref="InvalidOperationException">The registry value is unavailable or invalid.</exception>
    public static string ReadStableIdentity()
    {
        using var key = Registry.LocalMachine.OpenSubKey(CryptographyRegistryPath, writable: false)
            ?? throw new InvalidOperationException("The Windows installation identity is unavailable.");
        return FromMachineGuid(key.GetValue("MachineGuid") as string);
    }

    /// <summary>Canonicalizes a MachineGuid value into a namespaced consent identity.</summary>
    /// <param name="machineGuid">The Windows MachineGuid registry value.</param>
    /// <returns>The canonical PC identity.</returns>
    /// <exception cref="InvalidOperationException">The registry value is missing or is not a GUID.</exception>
    public static string FromMachineGuid(string? machineGuid)
    {
        if (!Guid.TryParse(machineGuid, out var guid) || guid == Guid.Empty)
            throw new InvalidOperationException("The Windows installation identity is missing or invalid.");
        return $"windows-machine-guid:{guid:N}";
    }
}
