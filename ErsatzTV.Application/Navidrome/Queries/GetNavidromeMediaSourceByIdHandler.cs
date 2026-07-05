using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Navidrome;

public class GetNavidromeMediaSourceByIdHandler : IRequestHandler<GetNavidromeMediaSourceById,
    Option<NavidromeMediaSourceViewModel>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;

    public GetNavidromeMediaSourceByIdHandler(IMediaSourceRepository mediaSourceRepository) =>
        _mediaSourceRepository = mediaSourceRepository;

    public Task<Option<NavidromeMediaSourceViewModel>> Handle(
        GetNavidromeMediaSourceById request,
        CancellationToken cancellationToken) =>
        _mediaSourceRepository.GetNavidrome(request.NavidromeMediaSourceId)
            .MapT(ms => new NavidromeMediaSourceViewModel(
                ms.Id,
                ms.ServerName,
                ms.Connections.HeadOrNone().Match(c => c.Address, () => string.Empty)));
}
