using System.Text.Json;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interrupts;
using LanguageExt;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

// watch folders: poll for newly arrived audio files and enqueue them as interrupts
// on the mapped channels. new files play at the next item boundary (priority 1),
// cut in (priority 0), or duck over content (style=duck).
//   GET    /api/watchfolders
//   PUT    /api/watchfolders          { name, path, channels, [priority], [style],
//                                       [duckPercent], [ttlSeconds], [enabled] }  (upsert by name)
//   DELETE /api/watchfolders/{name}
//
// the path is resolved inside the ErsatzRadio container. the watermark starts at
// the folder's registration time, so pre-existing files never play; only files
// arriving afterward do. ttlSeconds bounds how stale a file may air (default 3600).
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class WatchFoldersController : ControllerBase
{
    private readonly IConfigElementRepository _configElementRepository;

    public WatchFoldersController(IConfigElementRepository configElementRepository) =>
        _configElementRepository = configElementRepository;

    [HttpGet("api/watchfolders")]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken) =>
        Ok(await LoadFolders(cancellationToken));

    [HttpPut("api/watchfolders")]
    public async Task<IActionResult> Upsert(
        [FromBody] WatchFolderRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            return BadRequest(new { error = "name is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            return BadRequest(new { error = "path is required" });
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

        var folder = new WatchFolder(
            request.Name,
            request.Path,
            request.Channels,
            request.Priority ?? 1,
            (request.Style ?? "replace").ToLowerInvariant(),
            request.DuckPercent ?? 30,
            request.TtlSeconds ?? 3600,
            request.Enabled ?? true);

        List<WatchFolder> folders = await LoadFolders(cancellationToken);
        folders.RemoveAll(f => string.Equals(f.Name, folder.Name, StringComparison.OrdinalIgnoreCase));
        folders.Add(folder);

        await SaveFolders(folders, cancellationToken);

        object warning = Directory.Exists(folder.Path)
            ? null
            : $"path {folder.Path} does not exist (or is not visible inside the container)";

        return Ok(new { folders, warning });
    }

    [HttpDelete("api/watchfolders/{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken cancellationToken)
    {
        List<WatchFolder> folders = await LoadFolders(cancellationToken);
        int removed = folders.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            return NotFound();
        }

        await SaveFolders(folders, cancellationToken);

        return Ok(folders);
    }

    private async Task<List<WatchFolder>> LoadFolders(CancellationToken cancellationToken)
    {
        Option<string> maybeJson = await _configElementRepository.GetValue<string>(
            ConfigElementKey.WatchFolders,
            cancellationToken);

        foreach (string json in maybeJson.Filter(j => !string.IsNullOrWhiteSpace(j)))
        {
            try
            {
                return JsonSerializer.Deserialize<List<WatchFolder>>(json) ?? [];
            }
            catch
            {
                return [];
            }
        }

        return [];
    }

    private Task<Unit> SaveFolders(List<WatchFolder> folders, CancellationToken cancellationToken) =>
        _configElementRepository.Upsert(
            ConfigElementKey.WatchFolders,
            JsonSerializer.Serialize(folders),
            cancellationToken);

    public record WatchFolderRequest(
        string Name,
        string Path,
        List<string> Channels,
        int? Priority,
        string Style,
        int? DuckPercent,
        int? TtlSeconds,
        bool? Enabled);
}
