using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Audiobookshelf;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;

namespace ErsatzTV.Application.Audiobookshelf;

public class DisconnectAudiobookshelfHandler : IRequestHandler<DisconnectAudiobookshelf, Either<BaseError, Unit>>
{
    private readonly IAudiobookshelfSecretStore _audiobookshelfSecretStore;
    private readonly IEntityLocker _entityLocker;
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly ISearchIndex _searchIndex;

    public DisconnectAudiobookshelfHandler(
        IMediaSourceRepository mediaSourceRepository,
        IAudiobookshelfSecretStore audiobookshelfSecretStore,
        IEntityLocker entityLocker,
        ISearchIndex searchIndex)
    {
        _mediaSourceRepository = mediaSourceRepository;
        _audiobookshelfSecretStore = audiobookshelfSecretStore;
        _entityLocker = entityLocker;
        _searchIndex = searchIndex;
    }

    public async Task<Either<BaseError, Unit>> Handle(
        DisconnectAudiobookshelf request,
        CancellationToken cancellationToken)
    {
        List<int> ids = await _mediaSourceRepository.DeleteAllAudiobookshelf();
        await _searchIndex.RemoveItems(ids);
        _searchIndex.Commit();
        await _audiobookshelfSecretStore.DeleteAll();
        _entityLocker.UnlockRemoteMediaSource<AudiobookshelfMediaSource>();

        return Unit.Default;
    }
}
