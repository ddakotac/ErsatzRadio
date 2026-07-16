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

    /// <summary>
    ///     When set, the item is not eligible to play before this STREAM time. Because the
    ///     hls timeline tracks wall time, this is effectively the on-air time. The session
    ///     worker truncates the preceding scheduled transcode at this instant so the item
    ///     starts exactly on time (when enqueued far enough ahead of the air time).
    /// </summary>
    public DateTimeOffset? AirAt { get; init; }

    /// <summary>
    ///     Items that have not STARTED playing by this STREAM time are dropped.
    ///     For scheduled items this bounds lateness after the air time.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>True for files uploaded through the injection api; deleted after play/expiry.</summary>
    public bool DeleteFileWhenDone { get; init; }

    public InterruptStyle Style { get; init; } = InterruptStyle.Replace;

    /// <summary>Bed (scheduled content) volume while a duck-style item plays, 0..1.</summary>
    public double DuckBedVolume { get; init; } = 0.3;

    /// <summary>
    ///     When set, lifecycle webhooks fire to this url: "airing" when the item's
    ///     transcode starts, "completed" when it finishes, "expired" if the ttl passes
    ///     without airing.
    /// </summary>
    public string WebhookUrl { get; init; } = string.Empty;
}
