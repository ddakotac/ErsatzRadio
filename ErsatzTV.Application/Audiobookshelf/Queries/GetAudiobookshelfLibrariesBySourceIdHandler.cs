using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Audiobookshelf;

public class GetAudiobookshelfLibrariesBySourceIdHandler
    : IRequestHandler<GetAudiobookshelfLibrariesBySourceId, List<AudiobookshelfLibraryViewModel>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;

    public GetAudiobookshelfLibrariesBySourceIdHandler(IMediaSourceRepository mediaSourceRepository) =>
        _mediaSourceRepository = mediaSourceRepository;

    public async Task<List<AudiobookshelfLibraryViewModel>> Handle(
        GetAudiobookshelfLibrariesBySourceId request,
        CancellationToken cancellationToken)
    {
        List<AudiobookshelfLibrary> libraries =
            await _mediaSourceRepository.GetAudiobookshelfLibraries(request.AudiobookshelfMediaSourceId);
        return libraries
            .Map(l => new AudiobookshelfLibraryViewModel(l.Id, l.Name, l.MediaKind, l.ShouldSyncItems, l.MediaSourceId))
            .ToList();
    }
}
