namespace ErsatzTV.Application.Audiobookshelf;

public record GetAudiobookBooks(Option<int> ShowId) : IRequest<List<AudiobookBookViewModel>>;
