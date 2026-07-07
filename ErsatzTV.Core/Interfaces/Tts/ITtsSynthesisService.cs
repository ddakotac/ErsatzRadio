namespace ErsatzTV.Core.Interfaces.Tts;

public interface ITtsSynthesisService
{
    /// <summary>
    ///     Resolves a named TTS endpoint (falling back to the first registered endpoint,
    ///     then the legacy single url), synthesizes the text, and writes a wav file to the
    ///     interrupts folder. Returns the file path, or None on any failure (logged).
    /// </summary>
    Task<Option<string>> SynthesizeToFile(
        string text,
        string ttsEndpointName,
        string voiceOverride,
        CancellationToken cancellationToken);
}
