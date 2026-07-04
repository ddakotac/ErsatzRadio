using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Navidrome;

public class GetNavidromeLibrariesBySourceIdHandler
    : IRequestHandler<GetNavidromeLibrariesBySourceId, List<NavidromeLibraryViewModel>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;

    public GetNavidromeLibrariesBySourceIdHandler(IMediaSourceRepository mediaSourceRepository) =>
        _mediaSourceRepository = mediaSourceRepository;

    public async Task<List<NavidromeLibraryViewModel>> Handle(
        GetNavidromeLibrariesBySourceId request,
        CancellationToken cancellationToken)
    {
        List<NavidromeLibrary> libraries =
            await _mediaSourceRepository.GetNavidromeLibraries(request.NavidromeMediaSourceId);
        return libraries
            .Map(l => new NavidromeLibraryViewModel(l.Id, l.Name, l.MediaKind, l.ShouldSyncItems))
            .ToList();
    }
}
