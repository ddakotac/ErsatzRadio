namespace ErsatzTV.Application.Audiobookshelf;

public record GetAudiobookshelfMediaSourceById(int AudiobookshelfMediaSourceId)
    : IRequest<Option<AudiobookshelfMediaSourceViewModel>>;
