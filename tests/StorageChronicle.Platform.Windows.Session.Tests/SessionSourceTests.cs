using StorageChronicle.Platform.Windows.Session;
using Xunit;

namespace StorageChronicle.Platform.Windows.Session.Tests;

public sealed class SessionSourceTests
{
    [Fact]
    public void ClipboardGenerationSupportsRepeatedPasteAndClear()
    {
        var tracker = new ClipboardIntentTracker();
        tracker.Observe(4, ["C:\\source.txt"], false);
        Assert.NotNull(tracker.ConfirmPaste(4));
        Assert.NotNull(tracker.ConfirmPaste(4));
        tracker.ClearExcept(5);
        Assert.Null(tracker.ConfirmPaste(4));
    }

    [Fact]
    public void SessionGuardRejectsImpersonationAndNonClipboardMessages()
    {
        var guard = new SessionIpcGuard();
        Assert.True(guard.IsAllowed("S-1-5-21", "S-1-5-21", 3, 3, "ClipboardCandidate"));
        Assert.False(guard.IsAllowed("S-1-5-18", "S-1-5-21", 3, 3, "ClipboardCandidate"));
        Assert.False(guard.IsAllowed("S-1-5-21", "S-1-5-21", 3, 3, "Other"));
    }

    [Fact]
    public void ShareDiffKeepsShareChangesSeparate()
    {
        var differ = new ShareSnapshotDiffer();
        var before = new[] { new LocalShare("docs", "C:\\docs", "Disk", null, ["Users:Read"]) };
        var after = new[] { new LocalShare("docs", "D:\\docs", "Disk", null, ["Users:Read"]) };
        Assert.Equal("Changed", Assert.Single(differ.Diff(before, after)).ChangeKind);
    }
}
