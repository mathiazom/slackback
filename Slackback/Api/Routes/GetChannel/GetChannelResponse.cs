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

    public class MessageResponse
    {
        public required string Id { get; init; }
        public string? Text { get; init; }
        public required string AuthorName { get; init; }
        public required FileResponse[] Files { get; init; }
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
