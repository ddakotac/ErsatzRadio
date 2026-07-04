namespace ErsatzTV.Application.Audiobookshelf;

public record GetAudiobookshelfPathReplacementsBySourceId(int AudiobookshelfMediaSourceId)
    : IRequest<List<AudiobookshelfPathReplacementViewModel>>;
