using System.Text.Json;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interrupts;
using LanguageExt;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

// rss feeds: poll podcast feeds directly (no library or Audiobookshelf in the loop);
// newly published episodes are downloaded and enqueued as interrupts on the mapped
// channels - next boundary (priority 1), cut in (priority 0), or duck (style=duck).
//   GET    /api/rssfeeds
//   PUT    /api/rssfeeds          { name, url, channels, [priority], [style],
//                                   [duckPercent], [ttlSeconds], [enabled] }  (upsert by name)
//   DELETE /api/rssfeeds/{name}
//
// the watermark starts at registration time (by episode pubDate), so the feed's
// backlog never airs; only episodes published afterward do. feeds are polled
// every 5 minutes. ttlSeconds bounds staleness (default 3600).
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class RssFeedsController : ControllerBase
{
    private readonly IConfigElementRepository _configElementRepository;

    public RssFeedsController(IConfigElementRepository configElementRepository) =>
        _configElementRepository = configElementRepository;

    [HttpGet("api/rssfeeds")]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken) =>
        Ok(await LoadFeeds(cancellationToken));

    [HttpPut("api/rssfeeds")]
    public async Task<IActionResult> Upsert(
        [FromBody] RssFeedRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest(new { error = "name is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest(new { error = "url is required" });
        }

        if (request.Channels is not { Count: > 0 })
        {
            return BadRequest(new { error = "at least one channel is required" });
        }

        if (request.Style is not null &&
            !string.Equals(request.Style, "replace", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Style, "duck", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "style must be 'replace' or 'duck'" });
        }

        if (request.DuckPercent is < 0 or > 100)
        {
            return BadRequest(new { error = "duckPercent must be between 0 and 100" });
        }

        var folder = new RssFeed(
            request.Name,
            request.Url,
            request.Channels,
            request.Priority ?? 1,
            (request.Style ?? "replace").ToLowerInvariant(),
            request.DuckPercent ?? 30,
            request.TtlSeconds ?? 3600,
            request.Enabled ?? true);

        List<RssFeed> folders = await LoadFeeds(cancellationToken);
        folders.RemoveAll(f => string.Equals(f.Name, folder.Name, StringComparison.OrdinalIgnoreCase));
        folders.Add(folder);

        await SaveFeeds(folders, cancellationToken);

        object warning = Uri.TryCreate(folder.Url, UriKind.Absolute, out Uri parsed) &&
                         (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? null
            : $"url {folder.Url} is not a valid http(s) url";

        return Ok(new { feeds = folders, warning });
    }

    [HttpDelete("api/rssfeeds/{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken cancellationToken)
    {
        List<RssFeed> folders = await LoadFeeds(cancellationToken);
        int removed = folders.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            return NotFound();
        }

        await SaveFeeds(folders, cancellationToken);

        return Ok(folders);
    }

    private async Task<List<RssFeed>> LoadFeeds(CancellationToken cancellationToken)
    {
        Option<string> maybeJson = await _configElementRepository.GetValue<string>(
            ConfigElementKey.RssFeeds,
            cancellationToken);

        foreach (string json in maybeJson.Filter(j => !string.IsNullOrWhiteSpace(j)))
        {
            try
            {
                return JsonSerializer.Deserialize<List<RssFeed>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }

        return [];
    }

    private Task<Unit> SaveFeeds(List<RssFeed> folders, CancellationToken cancellationToken) =>
        _configElementRepository.Upsert(
            ConfigElementKey.RssFeeds,
            JsonSerializer.Serialize(folders),
            cancellationToken);

    public record RssFeedRequest(
        string Name,
        string Url,
        List<string> Channels,
        int? Priority,
        string Style,
        int? DuckPercent,
        int? TtlSeconds,
        bool? Enabled);
}
