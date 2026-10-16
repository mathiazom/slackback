namespace Api.Routes.GetChannel;

public class GetChannelResponse
{
    public class FileResponse
    {
        public required string Id { get; init; }
        public string? Name { get; init; }
        public string? Mimetype { get; init; }
        // The archived original, as uploaded.
        public required string Url { get; init; }
        // What to show inline: the original when browsers can display it, else
        // the converted copy slackpack made (e.g. JPEG for HEIC), null when
        // there's neither (the file is then only linked).
        public string? ImageUrl { get; init; }
        // Dimensions of ImageUrl's image, null when unknown (no image, or Slack
        // didn't say). Lets slackopy reserve space before loading.
        public int? Width { get; init; }
        public int? Height { get; init; }
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
        // Slack user id, null for bot/system messages.
        public string? AuthorId { get; init; }
        // The author's user snapshot as of this message; null before their first
        // snapshot. Lets slackopy tell when an author's identity changed.
        public int? AuthorSnapshotId { get; init; }
        public string? AuthorAvatarUrl { get; init; }
        public string? AuthorAvatarLargeUrl { get; init; }
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
