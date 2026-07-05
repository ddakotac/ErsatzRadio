using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Navidrome;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;

namespace ErsatzTV.Application.Navidrome;

public class DisconnectNavidromeHandler : IRequestHandler<DisconnectNavidrome, Either<BaseError, Unit>>
{
    private readonly IEntityLocker _entityLocker;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly INavidromeSecretStore _navidromeSecretStore;
    private readonly ISearchIndex _searchIndex;

    public DisconnectNavidromeHandler(
        IMediaSourceRepository mediaSourceRepository,
        INavidromeSecretStore navidromeSecretStore,
        IEntityLocker entityLocker,
        ISearchIndex searchIndex)
    {
        _mediaSourceRepository = mediaSourceRepository;
        _navidromeSecretStore = navidromeSecretStore;
        _entityLocker = entityLocker;
        _searchIndex = searchIndex;
    }

    public async Task<Either<BaseError, Unit>> Handle(
        DisconnectNavidrome request,
        CancellationToken cancellationToken)
    {
        List<int> ids = await _mediaSourceRepository.DeleteAllNavidrome();
        await _searchIndex.RemoveItems(ids);
        _searchIndex.Commit();
        await _navidromeSecretStore.DeleteAll();
        _entityLocker.UnlockRemoteMediaSource<NavidromeMediaSource>();

        return Unit.Default;
    }
}
