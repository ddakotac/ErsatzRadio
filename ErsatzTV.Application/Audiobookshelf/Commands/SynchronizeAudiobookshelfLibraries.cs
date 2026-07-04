using ErsatzTV.Core;

namespace ErsatzTV.Application.Audiobookshelf;

public record SynchronizeAudiobookshelfLibraries(int AudiobookshelfMediaSourceId) : IRequest<Either<BaseError, Unit>>,
    IScannerBackgroundServiceRequest;
