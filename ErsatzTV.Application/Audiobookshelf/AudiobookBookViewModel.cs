namespace ErsatzTV.Application.Audiobookshelf;

public record AudiobookBookViewModel(
    int SeasonId,
    int ShowId,
    string Title,
    string Author,
    int ChapterCount,
    string LibraryName,
    string Poster);
