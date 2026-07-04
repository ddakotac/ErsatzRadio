using ErsatzTV.Core.Audiobookshelf;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Core.Interfaces.Audiobookshelf;

public interface IAudiobookshelfTelevisionLibraryScanner
{
    Task<Either<BaseError, Unit>> ScanLibrary(
        AudiobookshelfConnectionParameters connectionParameters,
        AudiobookshelfLibrary library,
        bool deepScan,
        CancellationToken cancellationToken);
}
