namespace ErsatzTV.Application.Audiobookshelf;

public record GetAudiobookshelfLibrariesBySourceId(int AudiobookshelfMediaSourceId)
    : IRequest<List<AudiobookshelfLibraryViewModel>>;
