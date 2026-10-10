namespace Api.Routes.GetChannel;

public class GetChannelResponse
{
    public class FileResponse
    {
        public required string Id { get; init; }
        public string? Name { get; init; }
        public string? Mimetype { get; init; }
        public required string Url { get; init; }
    }

    public class ReactionUserResponse
    {
        public required string DisplayName { get; init; }
        public required string RealName { get; init; }
    }

    public class ReactionResponse
    {
        public required string Name { get; init; }
        public string? ImageUrl { get; init; }
        public required int Count { get; init; }
        public required ReactionUserResponse[] Users { get; init; }
    }

    public class MessageResponse
    {
        public required string Id { get; init; }
        public string? Text { get; init; }
        public required string AuthorDisplayName { get; init; }
        public required string AuthorRealName { get; init; }
        public required string Timestamp { get; init; }
        public required FileResponse[] Files { get; init; }
        public required ReactionResponse[] Reactions { get; init; }
        public required MessageResponse[] Replies { get; init; }
    }

    public class ChannelResponse
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Topic { get; init; }
    }

    public required ChannelResponse Channel { get; init; }
    public required MessageResponse[] Messages { get; init; }
}
