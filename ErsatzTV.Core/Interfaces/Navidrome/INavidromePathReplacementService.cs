using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Interfaces.Navidrome;

public interface INavidromePathReplacementService
{
    string GetReplacementNavidromePath(List<NavidromePathReplacement> pathReplacements, string path, bool log = true);
}
