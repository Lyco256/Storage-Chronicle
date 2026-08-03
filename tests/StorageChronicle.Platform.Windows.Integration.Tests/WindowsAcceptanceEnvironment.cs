using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace StorageChronicle.Platform.Windows.Integration.Tests;

internal static class WindowsAcceptanceEnvironment
{
    private const string RootVariable = "STORAGE_CHRONICLE_ACCEPTANCE_ROOT";
    private const string DeviceVariable = "STORAGE_CHRONICLE_ACCEPTANCE_DEVICE";
    private const string VhdxVariable = "STORAGE_CHRONICLE_ACCEPTANCE_VHDX";
    private const string RemovableVariable = "STORAGE_CHRONICLE_ACCEPTANCE_REMOVABLE_ROOT";
    private const string ShareVariable = "STORAGE_CHRONICLE_ACCEPTANCE_SMB_SHARE";
    private const string ServiceVariable = "STORAGE_CHRONICLE_ACCEPTANCE_SERVICE";
    private const string WaitForMediaVariable = "STORAGE_CHRONICLE_ACCEPTANCE_WAIT_FOR_MEDIA";

    public static string RootPath => Required(RootVariable);

    public static string DevicePath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(DeviceVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            var root = Path.GetPathRoot(RootPath);
            if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
            {
                throw new InvalidOperationException($"{DeviceVariable} is required when {RootVariable} is not mounted at a drive letter.");
            }

            return $"\\\\.\\{root[0]}:";
        }
    }

    public static string VhdxPath => Required(VhdxVariable);
    public static string RemovableRoot => Required(RemovableVariable);
    public static string ShareName => Required(ShareVariable);
    public static string ServiceName => Required(ServiceVariable);

    public static bool WaitForMediaChange => string.Equals(Environment.GetEnvironmentVariable(WaitForMediaVariable), "1", StringComparison.Ordinal);

    public static string Required(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{variable} is not configured. Run build/Test-Privileged.ps1 so this capability is reported as NOT_EXECUTED instead of being treated as a passing test.");
        }

        return value;
    }

    public static string CreateScenario(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var root = Path.GetFullPath(RootPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Acceptance root does not exist: {root}");
        }

        var scenario = Path.Combine(root, $"StorageChronicle.Acceptance.{name}.{Guid.NewGuid():N}");
        Directory.CreateDirectory(scenario);
        return scenario;
    }

    public static void DeleteScenario(string scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        if (Directory.Exists(scenario))
        {
            Directory.Delete(scenario, recursive: true);
        }
    }

    public static async Task<IReadOnlyList<T>> CollectAsync<T>(IAsyncEnumerable<T> values, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            result.Add(value);
        }

        return result;
    }

    public static async Task<bool> WaitForAsync(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        return ReferenceEquals(completed, task);
    }

    public static async Task<string> RunPowerShellAsync(string script, params string[] arguments)
    {
        var executable = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
        var startInfo = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PowerShell failed with exit code {process.ExitCode}: {error.Trim()}");
        }

        return output.Trim();
    }

}
