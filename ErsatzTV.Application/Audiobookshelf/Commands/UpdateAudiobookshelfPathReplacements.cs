using ErsatzTV.Core;

namespace ErsatzTV.Application.Audiobookshelf;

public record UpdateAudiobookshelfPathReplacements(
    int AudiobookshelfMediaSourceId,
    List<AudiobookshelfPathReplacementItem> PathReplacements) : IRequest<Either<BaseError, Unit>>;

public record AudiobookshelfPathReplacementItem(int Id, string AudiobookshelfPath, string LocalPath);
