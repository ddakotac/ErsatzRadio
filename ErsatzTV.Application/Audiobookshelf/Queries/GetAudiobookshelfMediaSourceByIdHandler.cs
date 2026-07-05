using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Audiobookshelf;

public class GetAudiobookshelfMediaSourceByIdHandler : IRequestHandler<GetAudiobookshelfMediaSourceById,
    Option<AudiobookshelfMediaSourceViewModel>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;

    public GetAudiobookshelfMediaSourceByIdHandler(IMediaSourceRepository mediaSourceRepository) =>
        _mediaSourceRepository = mediaSourceRepository;

    public Task<Option<AudiobookshelfMediaSourceViewModel>> Handle(
        GetAudiobookshelfMediaSourceById request,
        CancellationToken cancellationToken) =>
        _mediaSourceRepository.GetAudiobookshelf(request.AudiobookshelfMediaSourceId)
            .MapT(ms => new AudiobookshelfMediaSourceViewModel(
                ms.Id,
                ms.ServerName,
                ms.Connections.HeadOrNone().Match(c => c.Address, () => string.Empty)));
}
