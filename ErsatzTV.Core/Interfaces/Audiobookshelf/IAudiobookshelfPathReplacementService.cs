using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Interfaces.Audiobookshelf;

public interface IAudiobookshelfPathReplacementService
{
    string GetReplacementAbsPath(
        List<AudiobookshelfPathReplacement> pathReplacements,
        string path,
        bool log = true);
}
