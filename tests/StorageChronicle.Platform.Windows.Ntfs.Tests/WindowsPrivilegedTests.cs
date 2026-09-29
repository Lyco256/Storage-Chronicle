using StorageChronicle.Platform.Windows.Ntfs;
using Xunit;

namespace StorageChronicle.Platform.Windows.Ntfs.Tests;

public sealed class WindowsPrivilegedTests
{
    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    public void QueriesExistingJournalWithoutCreatingOrResizing()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_RUN_PRIVILEGED_NTFS"), "1", StringComparison.Ordinal)) return;

        var devicePath = Environment.GetEnvironmentVariable("STORAGE_CHRONICLE_NTFS_DEVICE") ?? "\\\\.\\C:";
        var api = new WindowsNtfsApi();
        try
        {
            using var handle = api.OpenVolume(devicePath);
            var result = api.QueryUsnJournal(handle, out var journal);
            Assert.True(result.Status is NtfsApiStatus.Success or NtfsApiStatus.JournalNotCreated, $"Unexpected status {result.Status} ({result.Win32Error}).");
            if (result.Succeeded)
            {
                Assert.NotNull(journal);
                Assert.True(journal!.MaximumSize > 0);
                Assert.True(journal.NextUsn >= journal.FirstUsn);
            }
        }
        catch (NtfsAccessException exception) when (exception.Status is NtfsApiStatus.AccessDenied or NtfsApiStatus.MediaRemoved)
        {
            // A protected or absent test volume is an expected environment limitation.
        }
    }
}
