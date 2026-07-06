namespace ErsatzTV.Core.Interrupts;

/// <summary>An audio file to mix over a scheduled transcode with the bed volume reduced.</summary>
public record DuckOverlay(string Path, TimeSpan Duration, double BedVolume);
