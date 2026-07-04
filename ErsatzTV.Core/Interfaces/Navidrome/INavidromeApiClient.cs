using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Navidrome;

namespace ErsatzTV.Core.Interfaces.Navidrome;

public interface INavidromeApiClient
{
    Task<Either<BaseError, NavidromeServerInformation>> GetServerInformation(
        NavidromeConnectionParameters connectionParameters);

    Task<Either<BaseError, List<NavidromeLibrary>>> GetLibraries(
        NavidromeConnectionParameters connectionParameters);

    IAsyncEnumerable<Tuple<NavidromeSong, int>> GetSongLibraryItems(
        NavidromeConnectionParameters connectionParameters,
        NavidromeLibrary library,
        CancellationToken cancellationToken = default);
}
