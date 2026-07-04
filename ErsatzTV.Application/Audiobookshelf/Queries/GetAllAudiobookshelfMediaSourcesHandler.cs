using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Audiobookshelf;

public class GetAllAudiobookshelfMediaSourcesHandler
    : IRequestHandler<GetAllAudiobookshelfMediaSources, List<AudiobookshelfMediaSourceViewModel>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;

    public GetAllAudiobookshelfMediaSourcesHandler(IMediaSourceRepository mediaSourceRepository) =>
        _mediaSourceRepository = mediaSourceRepository;

    public async Task<List<AudiobookshelfMediaSourceViewModel>> Handle(
        GetAllAudiobookshelfMediaSources request,
        CancellationToken cancellationToken)
    {
        List<AudiobookshelfMediaSource> mediaSources = await _mediaSourceRepository.GetAllAudiobookshelf(cancellationToken);
        return mediaSources
            .Map(ms => new AudiobookshelfMediaSourceViewModel(
                ms.Id,
                ms.ServerName,
                ms.Connections.HeadOrNone().Match(c => c.Address, () => string.Empty)))
            .ToList();
    }
}
