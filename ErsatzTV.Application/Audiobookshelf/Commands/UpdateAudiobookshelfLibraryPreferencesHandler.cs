using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;

namespace ErsatzTV.Application.Audiobookshelf;

public class UpdateAudiobookshelfLibraryPreferencesHandler
    : IRequestHandler<UpdateAudiobookshelfLibraryPreferences, Either<BaseError, Unit>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly ISearchIndex _searchIndex;

    public UpdateAudiobookshelfLibraryPreferencesHandler(
        IMediaSourceRepository mediaSourceRepository,
        ISearchIndex searchIndex)
    {
        _mediaSourceRepository = mediaSourceRepository;
        _searchIndex = searchIndex;
    }

    public async Task<Either<BaseError, Unit>> Handle(
        UpdateAudiobookshelfLibraryPreferences request,
        CancellationToken cancellationToken)
    {
        var toEnable = request.Preferences.Filter(p => p.ShouldSyncItems).Map(p => p.Id).ToList();
        var toDisable = request.Preferences.Filter(p => !p.ShouldSyncItems).Map(p => p.Id).ToList();

        List<int> ids = await _mediaSourceRepository.DisableAudiobookshelfLibrarySync(toDisable);
        if (ids.Count != 0)
        {
            await _searchIndex.RemoveItems(ids);
            _searchIndex.Commit();
        }

        await _mediaSourceRepository.EnableAudiobookshelfLibrarySync(toEnable);

        return Unit.Default;
    }
}
