using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Navidrome;

public class GetAllNavidromeMediaSourcesHandler
    : IRequestHandler<GetAllNavidromeMediaSources, List<NavidromeMediaSourceViewModel>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;

    public GetAllNavidromeMediaSourcesHandler(IMediaSourceRepository mediaSourceRepository) =>
        _mediaSourceRepository = mediaSourceRepository;

    public async Task<List<NavidromeMediaSourceViewModel>> Handle(
        GetAllNavidromeMediaSources request,
        CancellationToken cancellationToken)
    {
        List<NavidromeMediaSource> mediaSources = await _mediaSourceRepository.GetAllNavidrome(cancellationToken);
        return mediaSources
            .Map(ms => new NavidromeMediaSourceViewModel(
                ms.Id,
                ms.ServerName,
                ms.Connections.HeadOrNone().Match(c => c.Address, () => string.Empty)))
            .ToList();
    }
}
