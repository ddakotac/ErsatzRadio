using System.Text.RegularExpressions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Audiobookshelf;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Audiobookshelf;

public class AudiobookshelfPathReplacementService : IAudiobookshelfPathReplacementService
{
    private readonly ILogger<AudiobookshelfPathReplacementService> _logger;

    public AudiobookshelfPathReplacementService(ILogger<AudiobookshelfPathReplacementService> logger) =>
        _logger = logger;

    public string GetReplacementAbsPath(
        List<AudiobookshelfPathReplacement> pathReplacements,
        string path,
        bool log = true)
    {
        Option<AudiobookshelfPathReplacement> maybeReplacement = pathReplacements
            .Filter(r => r.AudiobookshelfPath is not null && r.LocalPath is not null)
            .Filter(r => string.IsNullOrEmpty(r.AudiobookshelfPath) ||
                         path.StartsWith(r.AudiobookshelfPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.AudiobookshelfPath.Length)
            .HeadOrNone();

        foreach (AudiobookshelfPathReplacement replacement in maybeReplacement)
        {
            string finalPath;
            if (string.IsNullOrEmpty(replacement.AudiobookshelfPath))
            {
                finalPath = Path.Combine(replacement.LocalPath, path.TrimStart('/', '\\'));
            }
            else
            {
                finalPath = Regex.Replace(
                    path,
                    Regex.Escape(replacement.AudiobookshelfPath),
                    Regex.Replace(replacement.LocalPath ?? string.Empty, "\\$[0-9]+", @"$$$0"),
                    RegexOptions.IgnoreCase);
            }

            finalPath = finalPath.Replace(@"\", @"/");

            if (log)
            {
                _logger.LogInformation(
                    "Replacing audiobookshelf path {AbsPath} with {LocalPath} resulting in {FinalPath}",
                    replacement.AudiobookshelfPath,
                    replacement.LocalPath,
                    finalPath);
            }

            return finalPath;
        }

        return path;
    }
}
