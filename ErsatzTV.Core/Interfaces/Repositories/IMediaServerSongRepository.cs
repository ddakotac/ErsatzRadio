using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Metadata;

namespace ErsatzTV.Core.Interfaces.Repositories;

public interface IMediaServerSongRepository<in TLibrary, TSong, TEtag> where TLibrary : Library
    where TSong : Song
    where TEtag : MediaServerItemEtag
{
    Task<List<TEtag>> GetExistingSongs(TLibrary library);
    Task<Option<int>> FlagNormal(TLibrary library, TSong song);
    Task<Option<int>> FlagUnavailable(TLibrary library, TSong song);
    Task<List<int>> FlagFileNotFound(TLibrary library, List<string> songItemIds);

    Task<Either<BaseError, MediaItemScanResult<TSong>>> GetOrAdd(
        TLibrary library,
        TSong item,
        bool deepScan,
        CancellationToken cancellationToken);

    Task<Unit> SetEtag(TSong song, string etag);
}
