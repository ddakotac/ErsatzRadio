namespace ErsatzTV.Core.Interrupts;

public record InterruptQueueItem
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string ChannelNumber { get; init; }

    /// <summary>Absolute local path to the audio file to inject.</summary>
    public string Path { get; init; }

    public string Title { get; init; }

    /// <summary>
    ///     Lower value = higher priority. Priority 0 is an emergency interrupt that
    ///     kills the currently-transcoding scheduled item; all other priorities wait
    ///     for the next item boundary. FIFO within the same priority.
    /// </summary>
    public int Priority { get; init; } = 1;

    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Items that have not STARTED playing by this time are dropped.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>True for files uploaded through the injection api; deleted after play/expiry.</summary>
    public bool DeleteFileWhenDone { get; init; }
}
