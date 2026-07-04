using ErsatzTV.Core;

namespace ErsatzTV.Scanner.Application.Audiobookshelf;

public record SynchronizeAudiobookshelfLibraryById(
    string BaseUrl,
    int AudiobookshelfLibraryId,
    bool ForceScan,
    bool DeepScan) : IRequest<Either<BaseError, string>>;
