namespace ErsatzTV.Core.Tts;

/// <summary>
///     A named TTS endpoint. Url schemes: http(s):// (POST plain text, audio bytes back)
///     or wyoming://host:port (wyoming-piper et al). Voice is the default voice for this
///     endpoint; channels may override it.
/// </summary>
public record TtsEndpoint(string Name, string Url, string Voice);
