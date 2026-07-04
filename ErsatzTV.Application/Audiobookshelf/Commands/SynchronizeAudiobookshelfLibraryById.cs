using ErsatzTV.Core;

namespace ErsatzTV.Application.Audiobookshelf;

public interface ISynchronizeAudiobookshelfLibraryById : IRequest<Either<BaseError, string>>,
    IScannerBackgroundServiceRequest
{
    int AudiobookshelfLibraryId { get; }
    bool ForceScan { get; }
    bool DeepScan { get; }
}

public record SynchronizeAudiobookshelfLibraryByIdIfNeeded(int AudiobookshelfLibraryId) : ISynchronizeAudiobookshelfLibraryById
{
    public bool ForceScan => false;
    public bool DeepScan => false;
}

public record ForceSynchronizeAudiobookshelfLibraryById(int AudiobookshelfLibraryId, bool DeepScan)
    : ISynchronizeAudiobookshelfLibraryById
{
    public bool ForceScan => true;
}
