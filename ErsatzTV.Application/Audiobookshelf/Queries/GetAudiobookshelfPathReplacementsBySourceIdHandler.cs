using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Audiobookshelf;

public class GetAudiobookshelfPathReplacementsBySourceIdHandler
    : IRequestHandler<GetAudiobookshelfPathReplacementsBySourceId, List<AudiobookshelfPathReplacementViewModel>>
{
    private readonly IMediaSourceRepository _mediaSourceRepository;

    public GetAudiobookshelfPathReplacementsBySourceIdHandler(IMediaSourceRepository mediaSourceRepository) =>
        _mediaSourceRepository = mediaSourceRepository;

    public async Task<List<AudiobookshelfPathReplacementViewModel>> Handle(
        GetAudiobookshelfPathReplacementsBySourceId request,
        CancellationToken cancellationToken)
    {
        List<AudiobookshelfPathReplacement> replacements =
            await _mediaSourceRepository.GetAudiobookshelfPathReplacements(request.AudiobookshelfMediaSourceId);
        return replacements
            .Map(r => new AudiobookshelfPathReplacementViewModel(r.Id, r.AudiobookshelfPath, r.LocalPath))
            .ToList();
    }
}
