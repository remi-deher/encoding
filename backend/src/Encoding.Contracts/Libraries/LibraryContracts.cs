namespace Encoding.Contracts.Libraries;

public enum LibraryMediaType
{
    Video,
    Image,
    Music,
    Subtitles
}

public sealed record CreateLibraryRequest(string Name, LibraryMediaType MediaType);

public sealed record LibraryResponse(Guid Id, string Name, LibraryMediaType MediaType, DateTimeOffset CreatedAt);
