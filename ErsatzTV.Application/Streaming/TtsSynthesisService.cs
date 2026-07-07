using System.Text.Json;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Tts;
using ErsatzTV.Core.Tts;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class TtsSynthesisService : ITtsSynthesisService
{
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TtsSynthesisService> _logger;

    private bool _warnedNoTtsUrl;

    public TtsSynthesisService(
        IConfigElementRepository configElementRepository,
        IHttpClientFactory httpClientFactory,
        ILogger<TtsSynthesisService> logger)
    {
        _configElementRepository = configElementRepository;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<Option<string>> SynthesizeToFile(
        string text,
        string ttsEndpointName,
        string voiceOverride,
        CancellationToken cancellationToken)
    {
        Option<(string Url, string Voice)> maybeTarget =
            await ResolveTtsTarget(ttsEndpointName, voiceOverride, cancellationToken);

        foreach ((string url, string voice) in maybeTarget)
        {
            try
            {
                byte[] audio;

                if (url.StartsWith("wyoming://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(url.Replace("wyoming://", "tcp://", StringComparison.OrdinalIgnoreCase));
                    int port = uri.IsDefaultPort ? 10200 : uri.Port;

                    audio = await WyomingTtsClient.Synthesize(
                        uri.Host,
                        port,
                        text,
                        Optional(voice).Filter(v => !string.IsNullOrWhiteSpace(v)),
                        TimeSpan.FromSeconds(30),
                        cancellationToken);
                }
                else
                {
                    HttpClient client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(30);

                    using var content = new StringContent(text);
                    using HttpResponseMessage response = await client.PostAsync(url, content, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "TTS endpoint returned {StatusCode}; skipping synthesis",
                            (int)response.StatusCode);

                        return Option<string>.None;
                    }

                    audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                }

                if (audio.Length == 0)
                {
                    _logger.LogWarning("TTS endpoint returned no audio; skipping synthesis");
                    return Option<string>.None;
                }

                string path = Path.Combine(FileSystemLayout.InterruptsFolder, $"tts-{Guid.NewGuid()}.wav");
                await File.WriteAllBytesAsync(path, audio, cancellationToken);
                return path;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TTS request failed; skipping synthesis");
                return Option<string>.None;
            }
        }

        if (!_warnedNoTtsUrl)
        {
            _warnedNoTtsUrl = true;
            _logger.LogWarning(
                "TTS was requested but no endpoint is configured or the configured endpoint name was not found");
        }

        return Option<string>.None;
    }

    private async Task<Option<(string Url, string Voice)>> ResolveTtsTarget(
        string ttsEndpointName,
        string voiceOverride,
        CancellationToken cancellationToken)
    {
        List<TtsEndpoint> endpoints = await LoadEndpoints(cancellationToken);

        // named endpoint
        if (!string.IsNullOrWhiteSpace(ttsEndpointName))
        {
            foreach (TtsEndpoint endpoint in Optional(
                         endpoints.Find(e => string.Equals(e.Name, ttsEndpointName, StringComparison.OrdinalIgnoreCase))))
            {
                return (endpoint.Url, FirstNonEmpty(voiceOverride, endpoint.Voice));
            }

            _logger.LogWarning(
                "TTS endpoint {Name} was not found in the endpoints registry",
                ttsEndpointName);

            return Option<(string, string)>.None;
        }

        // first registered endpoint
        foreach (TtsEndpoint endpoint in endpoints.HeadOrNone())
        {
            return (endpoint.Url, FirstNonEmpty(voiceOverride, endpoint.Voice));
        }

        // legacy single url
        Option<string> maybeLegacyUrl = await _configElementRepository.GetValue<string>(
            ConfigElementKey.AnnouncerTtsUrl,
            cancellationToken);

        foreach (string legacyUrl in maybeLegacyUrl.Filter(u => !string.IsNullOrWhiteSpace(u)))
        {
            return (legacyUrl, voiceOverride);
        }

        return Option<(string, string)>.None;
    }

    private async Task<List<TtsEndpoint>> LoadEndpoints(CancellationToken cancellationToken)
    {
        Option<string> maybeJson = await _configElementRepository.GetValue<string>(
            ConfigElementKey.AnnouncerTtsEndpoints,
            cancellationToken);

        foreach (string json in maybeJson.Filter(j => !string.IsNullOrWhiteSpace(j)))
        {
            try
            {
                return JsonSerializer.Deserialize<List<TtsEndpoint>>(json) ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse tts endpoints; ignoring");
            }
        }

        return [];
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
