using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Navidrome;

public class GetNavidromePathReplacementsBySourceIdHandler
    : IRequestHandler<GetNavidromePathReplacementsBySourceId, List<NavidromePathReplacementViewModel>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;

    public GetNavidromePathReplacementsBySourceIdHandler(IMediaSourceRepository mediaSourceRepository) =>
        _mediaSourceRepository = mediaSourceRepository;

    public async Task<List<NavidromePathReplacementViewModel>> Handle(
        GetNavidromePathReplacementsBySourceId request,
        CancellationToken cancellationToken)
    {
        List<NavidromePathReplacement> replacements =
            await _mediaSourceRepository.GetNavidromePathReplacements(request.NavidromeMediaSourceId);
        return replacements
            .Map(r => new NavidromePathReplacementViewModel(r.Id, r.NavidromePath, r.LocalPath))
            .ToList();
    }
}
