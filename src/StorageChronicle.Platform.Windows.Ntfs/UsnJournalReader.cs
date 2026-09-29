using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace StorageChronicle.Platform.Windows.Ntfs;

/// <summary>Bounds one streamed USN read and controls whether the native call waits.</summary>
public sealed record UsnReadOptions
{
    /// <summary>Gets the output buffer size in bytes.</summary>
    public int BufferSize { get; init; } = 64 * 1024;
    /// <summary>Gets the maximum wait used by FSCTL_READ_USN_JOURNAL.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Gets the USN reason mask requested from the journal.</summary>
    public int ReasonMask { get; init; } = -1;

    /// <summary>Validates bounded read options.</summary>
    public void Validate()
    {
        if (BufferSize < 4096) throw new ArgumentOutOfRangeException(nameof(BufferSize), "The USN buffer must be at least 4096 bytes.");
        if (WaitTimeout < TimeSpan.Zero || WaitTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(WaitTimeout));
    }
}

/// <summary>Reads a volume's existing journal through the FSCTL boundary.</summary>
public sealed class UsnJournalReader : IUsnJournalReader
{
    private readonly INtfsApi api;
    private readonly string devicePath;
    private readonly UsnReadOptions options;
    private readonly UsnJournalState? previousState;

    /// <summary>Initializes a reader; it never creates or resizes a journal.</summary>
    public UsnJournalReader(INtfsApi api, string devicePath, UsnReadOptions? options = null, UsnJournalState? previousState = null)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.devicePath = string.IsNullOrWhiteSpace(devicePath) ? throw new ArgumentException("A volume device path is required.", nameof(devicePath)) : devicePath;
        this.options = options ?? new UsnReadOptions();
        this.options.Validate();
        this.previousState = previousState;
    }

    /// <summary>Gets the latest Journal ID and next USN observed by the reader.</summary>
    public UsnJournalState? LastObservedState { get; private set; }

    /// <inheritdoc />
    public async IAsyncEnumerable<UsnReadResult> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var handle = api.OpenVolume(devicePath);
        var query = api.QueryUsnJournal(handle, out var journal);
        if (!query.Succeeded || journal is null)
        {
            if (query.Status == NtfsApiStatus.JournalNotCreated)
            {
                yield return Gap(query, "JournalNotCreated");
                yield break;
            }

            yield return Gap(query, query.Status == NtfsApiStatus.AccessDenied ? "AccessDenied" : "JournalQueryFailed");
            yield break;
        }

        LastObservedState = new UsnJournalState(journal.JournalId, journal.NextUsn);
        var startUsn = DetermineStartUsn(journal, out var continuityGap);
        if (continuityGap is not null)
        {
            yield return new UsnReadResult(journal.NextUsn, null, true, continuityGap);
            yield break;
        }

        var buffer = new byte[options.BufferSize];
        var recovered = previousState is not null;
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ReadUsnJournalRequest(startUsn, options.ReasonMask, false, options.WaitTimeout, 1, journal.JournalId, journal.MinSupportedMajorVersion, journal.MaxSupportedMajorVersion);
            var read = api.ReadUsnJournal(handle, request, buffer, out var bytesReturned);
            if (!read.Succeeded)
            {
                if (read.Status == NtfsApiStatus.MediaRemoved || read.Status == NtfsApiStatus.AccessDenied)
                {
                    yield return Gap(read, read.Status == NtfsApiStatus.AccessDenied ? "AccessDenied" : "MediaRemoved");
                }
                else if (read.Status != NtfsApiStatus.JournalNotCreated)
                {
                    yield return Gap(read, "JournalReadFailed");
                }

                yield break;
            }

            if (bytesReturned < sizeof(long)) yield break;
            var records = new UsnRecordParser().ParseReadBuffer(buffer.AsSpan(0, bytesReturned), out var nextUsn);
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new UsnReadResult(record.Usn, record, recovered, null);
            }

            if (records.Count == 0 || nextUsn <= startUsn || nextUsn >= journal.NextUsn)
            {
                yield break;
            }

            startUsn = nextUsn;
            LastObservedState = new UsnJournalState(journal.JournalId, startUsn);
            await Task.Yield();
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private long DetermineStartUsn(UsnJournalData journal, out string? gap)
    {
        gap = null;
        if (previousState is null) return journal.NextUsn;
        if (previousState.JournalId != journal.JournalId)
        {
            gap = "JournalIdChanged";
            return journal.NextUsn;
        }

        if (previousState.NextUsn < journal.LowestValidUsn || previousState.NextUsn < journal.FirstUsn)
        {
            gap = "JournalTruncated";
            return journal.NextUsn;
        }

        return Math.Min(previousState.NextUsn, journal.NextUsn);
    }

    private static UsnReadResult Gap(NtfsApiCallResult result, string reason) => new(result.BytesReturned, null, true, $"{reason}:{result.Win32Error}");
}
