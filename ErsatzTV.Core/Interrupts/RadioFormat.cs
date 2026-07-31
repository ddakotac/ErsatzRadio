namespace ErsatzTV.Core.Interrupts;

/// <summary>Wire format for the direct radio endpoints.</summary>
public enum RadioFormat
{
    /// <summary>Stream copy of the segments' aac: cheapest, original quality.</summary>
    Aac = 0,

    /// <summary>Transcode to mp3: widest client support, optional gap filling.</summary>
    Mp3 = 1
}
