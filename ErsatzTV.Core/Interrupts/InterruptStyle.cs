namespace ErsatzTV.Core.Interrupts;

public enum InterruptStyle
{
    /// <summary>Interrupt audio replaces scheduled content for its duration.</summary>
    Replace = 0,

    /// <summary>
    ///     Interrupt audio is mixed OVER scheduled content, which continues underneath at
    ///     reduced volume (an "audio overlay" - chimes, announcements, station idents).
    /// </summary>
    Duck = 1
}
