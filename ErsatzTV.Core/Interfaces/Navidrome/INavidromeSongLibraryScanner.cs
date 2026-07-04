using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Navidrome;

namespace ErsatzTV.Core.Interfaces.Navidrome;

public interface INavidromeSongLibraryScanner
{
    Task<Either<BaseError, Unit>> ScanLibrary(
        NavidromeConnectionParameters connectionParameters,
        NavidromeLibrary library,
        bool deepScan,
        CancellationToken cancellationToken);
}
