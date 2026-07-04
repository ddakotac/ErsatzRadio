using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;

namespace ErsatzTV.Application.Navidrome;

public class UpdateNavidromeLibraryPreferencesHandler
    : IRequestHandler<UpdateNavidromeLibraryPreferences, Either<BaseError, Unit>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;
    private readonly ISearchIndex _searchIndex;

    public UpdateNavidromeLibraryPreferencesHandler(
        IMediaSourceRepository mediaSourceRepository,
        ISearchIndex searchIndex)
    {
        _mediaSourceRepository = mediaSourceRepository;
        _searchIndex = searchIndex;
    }

    public async Task<Either<BaseError, Unit>> Handle(
        UpdateNavidromeLibraryPreferences request,
        CancellationToken cancellationToken)
    {
        var toEnable = request.Preferences.Filter(p => p.ShouldSyncItems).Map(p => p.Id).ToList();
        var toDisable = request.Preferences.Filter(p => !p.ShouldSyncItems).Map(p => p.Id).ToList();

        List<int> ids = await _mediaSourceRepository.DisableNavidromeLibrarySync(toDisable);
        if (ids.Count != 0)
        {
            await _searchIndex.RemoveItems(ids);
            _searchIndex.Commit();
        }

        await _mediaSourceRepository.EnableNavidromeLibrarySync(toEnable);

        return Unit.Default;
    }
}
