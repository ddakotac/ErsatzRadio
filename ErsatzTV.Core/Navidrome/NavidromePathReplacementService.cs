using System.Text.RegularExpressions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Navidrome;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Navidrome;

public class NavidromePathReplacementService : INavidromePathReplacementService
{
    private readonly ILogger<NavidromePathReplacementService> _logger;

    public NavidromePathReplacementService(ILogger<NavidromePathReplacementService> logger) => _logger = logger;

    public string GetReplacementNavidromePath(
        List<NavidromePathReplacement> pathReplacements,
        string path,
        bool log = true)
    {
        // subsonic song paths are relative to the music folder root; an empty
        // NavidromePath prefix matches everything and prepends the local path
        Option<NavidromePathReplacement> maybeReplacement = pathReplacements
            .Filter(r => r.NavidromePath is not null && r.LocalPath is not null)
            .Filter(r => string.IsNullOrEmpty(r.NavidromePath) ||
                         path.StartsWith(r.NavidromePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.NavidromePath.Length)
            .HeadOrNone();

        foreach (NavidromePathReplacement replacement in maybeReplacement)
        {
            string finalPath;
            if (string.IsNullOrEmpty(replacement.NavidromePath))
            {
                finalPath = Path.Combine(replacement.LocalPath, path.TrimStart('/', '\\'));
            }
            else
            {
                finalPath = Regex.Replace(
                    path,
                    Regex.Escape(replacement.NavidromePath),
                    Regex.Replace(replacement.LocalPath ?? string.Empty, "\\$[0-9]+", @"$$$0"),
                    RegexOptions.IgnoreCase);
            }

            finalPath = finalPath.Replace(@"\", @"/");

            if (log)
            {
                _logger.LogInformation(
                    "Replacing navidrome path {NavidromePath} with {LocalPath} resulting in {FinalPath}",
                    replacement.NavidromePath,
                    replacement.LocalPath,
                    finalPath);
            }

            return finalPath;
        }

        return path;
    }
}
