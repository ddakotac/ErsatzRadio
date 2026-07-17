using ErsatzTV.Application.Channels;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Tts;
using ErsatzTV.Core.Tts;
using LanguageExt;
using MediatR;
using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

// configuration for the per-channel "now playing" tts announcer:
//   GET /api/channels/{channelNumber}/announcer
//   PUT /api/channels/{channelNumber}/announcer  { enabled, template, style, duckPercent }
//   GET /api/announcer/tts
//   PUT /api/announcer/tts  { url }
//
//   GET    /api/announcer/tts/endpoints
//   PUT    /api/announcer/tts/endpoints        { name, url, voice? }   (upsert by name)
//   DELETE /api/announcer/tts/endpoints/{name}
//
// endpoint urls: http(s):// (POST plain text -> audio bytes, e.g. piper http server) or
// wyoming://host:port (wyoming-piper; voice selects the piper voice by name). channels
// reference endpoints by name via ttsEndpoint and may override voice. template variables:
// {title} {artist} {album} {show}/{author} {season}/{book}. style: duck (default; mixes
// over the item's opening at duckPercent bed volume) or replace.
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class AnnouncerController : ControllerBase
{
    private readonly IChannelAnnouncerService _announcerService;
    private readonly IMediator _mediator;
    private readonly IConfigElementRepository _configElementRepository;
    private readonly ITtsSynthesisService _ttsSynthesisService;

    public AnnouncerController(
        IMediator mediator,
        IConfigElementRepository configElementRepository,
        ITtsSynthesisService ttsSynthesisService,
        IChannelAnnouncerService announcerService)
    {
        _announcerService = announcerService;
        _mediator = mediator;
        _configElementRepository = configElementRepository;
        _ttsSynthesisService = ttsSynthesisService;
    }

    [HttpGet("api/channels/{channelNumber}/announcer")]
    public async Task<IActionResult> GetChannelAnnouncer(string channelNumber, CancellationToken cancellationToken)
    {
        Option<ChannelViewModel> maybeChannel = await _mediator.Send(
            new GetChannelByNumber(channelNumber),
            cancellationToken);

        if (maybeChannel.IsNone)
        {
            return NotFound(new { error = $"channel {channelNumber} does not exist" });
        }

        bool enabled = await _configElementRepository
            .GetValue<bool>(ConfigElementKey.AnnouncerEnabled(channelNumber), cancellationToken)
            .Map(o => o.IfNone(false));

        string template = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerTemplate(channelNumber), cancellationToken)
            .Map(o => o.IfNone("Now playing: {title}"));

        string style = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerStyle(channelNumber), cancellationToken)
            .Map(o => o.IfNone("duck"));

        int duckPercent = await _configElementRepository
            .GetValue<int>(ConfigElementKey.AnnouncerDuckPercent(channelNumber), cancellationToken)
            .Map(o => o.IfNone(30));

        string ttsEndpoint = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerTtsEndpoint(channelNumber), cancellationToken)
            .Map(o => o.IfNone(string.Empty));

        string voice = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerVoice(channelNumber), cancellationToken)
            .Map(o => o.IfNone(string.Empty));

        return Ok(new { channelNumber, enabled, template, style, duckPercent, ttsEndpoint, voice });
    }

    [HttpPut("api/channels/{channelNumber}/announcer")]
    public async Task<IActionResult> SetChannelAnnouncer(
        string channelNumber,
        [FromBody] AnnouncerConfigRequest request,
        CancellationToken cancellationToken)
    {
        Option<ChannelViewModel> maybeChannel = await _mediator.Send(
            new GetChannelByNumber(channelNumber),
            cancellationToken);

        if (maybeChannel.IsNone)
        {
            return NotFound(new { error = $"channel {channelNumber} does not exist" });
        }

        if (request.Style is not null &&
            !string.Equals(request.Style, "duck", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Style, "replace", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "style must be 'duck' or 'replace'" });
        }

        if (request.DuckPercent is < 0 or > 100)
        {
            return BadRequest(new { error = "duckPercent must be between 0 and 100" });
        }

        if (request.Enabled.HasValue)
        {
            await _configElementRepository.Upsert(
                ConfigElementKey.AnnouncerEnabled(channelNumber),
                request.Enabled.Value,
                cancellationToken);
        }

        if (request.Template is not null)
        {
            await _configElementRepository.Upsert(
                ConfigElementKey.AnnouncerTemplate(channelNumber),
                request.Template,
                cancellationToken);
        }

        if (request.Style is not null)
        {
            await _configElementRepository.Upsert(
                ConfigElementKey.AnnouncerStyle(channelNumber),
                request.Style.ToLowerInvariant(),
                cancellationToken);
        }

        if (request.DuckPercent.HasValue)
        {
            await _configElementRepository.Upsert(
                ConfigElementKey.AnnouncerDuckPercent(channelNumber),
                request.DuckPercent.Value,
                cancellationToken);
        }

        if (request.TtsEndpoint is not null)
        {
            await _configElementRepository.Upsert(
                ConfigElementKey.AnnouncerTtsEndpoint(channelNumber),
                request.TtsEndpoint,
                cancellationToken);
        }

        if (request.Voice is not null)
        {
            await _configElementRepository.Upsert(
                ConfigElementKey.AnnouncerVoice(channelNumber),
                request.Voice,
                cancellationToken);
        }

        return await GetChannelAnnouncer(channelNumber, cancellationToken);
    }

    [HttpGet("api/announcer/tts")]
    public async Task<IActionResult> GetTts(CancellationToken cancellationToken)
    {
        string url = await _configElementRepository
            .GetValue<string>(ConfigElementKey.AnnouncerTtsUrl, cancellationToken)
            .Map(o => o.IfNone(string.Empty));

        return Ok(new { url });
    }

    [HttpPut("api/announcer/tts")]
    public async Task<IActionResult> SetTts(
        [FromBody] AnnouncerTtsRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Url) ||
            !Uri.TryCreate(request.Url, UriKind.Absolute, out _))
        {
            return BadRequest(new { error = "url must be a valid absolute url" });
        }

        await _configElementRepository.Upsert(ConfigElementKey.AnnouncerTtsUrl, request.Url, cancellationToken);

        return Ok(new { url = request.Url });
    }

    // synthesize a test phrase and return the audio (for the settings page preview button)
    [HttpGet("api/announcer/tts/preview")]
    public async Task<IActionResult> PreviewTts(
        [FromQuery] string text,
        [FromQuery] string endpoint,
        [FromQuery] string voice,
        [FromQuery] string template,
        [FromQuery] string channel,
        CancellationToken cancellationToken)
    {
        // when the channel is actually playing something, preview against the LIVE
        // item's metadata instead of the sample text
        if (!string.IsNullOrWhiteSpace(template) && !string.IsNullOrWhiteSpace(channel))
        {
            Option<string> maybeLive = await _announcerService.RenderTemplateForCurrentItem(
                channel,
                template,
                cancellationToken);

            foreach (string live in maybeLive)
            {
                text = live;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return BadRequest(new { error = "text is required" });
        }

        Option<string> maybePath = await _ttsSynthesisService.SynthesizeToFile(
            text,
            endpoint,
            voice,
            cancellationToken);

        foreach (string path in maybePath)
        {
            try
            {
                byte[] audio = await System.IO.File.ReadAllBytesAsync(path, cancellationToken);
                return File(audio, "audio/wav");
            }
            finally
            {
                try
                {
                    System.IO.File.Delete(path);
                }
                catch
                {
                    // best effort
                }
            }
        }

        return StatusCode(
            502,
            new { error = "tts synthesis failed; check the tts endpoint configuration and logs" });
    }

    [HttpGet("api/announcer/tts/endpoints")]
    public async Task<IActionResult> GetTtsEndpoints(CancellationToken cancellationToken) =>
        Ok(await LoadEndpoints(cancellationToken));

    [HttpPut("api/announcer/tts/endpoints")]
    public async Task<IActionResult> UpsertTtsEndpoint(
        [FromBody] TtsEndpointRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest(new { error = "name is required" });
        }

        bool isWyoming = request.Url?.StartsWith("wyoming://", StringComparison.OrdinalIgnoreCase) == true;
        bool isHttp = Uri.TryCreate(request.Url, UriKind.Absolute, out Uri parsed) &&
                      (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

        if (!isWyoming && !isHttp)
        {
            return BadRequest(new { error = "url must be http(s):// or wyoming://host:port" });
        }

        List<TtsEndpoint> endpoints = await LoadEndpoints(cancellationToken);
        endpoints.RemoveAll(e => string.Equals(e.Name, request.Name, StringComparison.OrdinalIgnoreCase));
        endpoints.Add(new TtsEndpoint(request.Name, request.Url, request.Voice ?? string.Empty));

        await SaveEndpoints(endpoints, cancellationToken);

        return Ok(endpoints);
    }

    [HttpDelete("api/announcer/tts/endpoints/{name}")]
    public async Task<IActionResult> DeleteTtsEndpoint(string name, CancellationToken cancellationToken)
    {
        List<TtsEndpoint> endpoints = await LoadEndpoints(cancellationToken);
        int removed = endpoints.RemoveAll(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            return NotFound();
        }

        await SaveEndpoints(endpoints, cancellationToken);

        return Ok(endpoints);
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
                return System.Text.Json.JsonSerializer.Deserialize<List<TtsEndpoint>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }

        return [];
    }

    private Task<Unit> SaveEndpoints(List<TtsEndpoint> endpoints, CancellationToken cancellationToken) =>
        _configElementRepository.Upsert(
            ConfigElementKey.AnnouncerTtsEndpoints,
            System.Text.Json.JsonSerializer.Serialize(endpoints),
            cancellationToken);

    public record AnnouncerConfigRequest(
        bool? Enabled,
        string Template,
        string Style,
        int? DuckPercent,
        string TtsEndpoint,
        string Voice);

    public record AnnouncerTtsRequest(string Url);

    public record TtsEndpointRequest(string Name, string Url, string Voice);
}
