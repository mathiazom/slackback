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
        // How to show the file inline, null when it's only linked.
        public EmbedResponse? Embed { get; init; }
    }

    public class EmbedResponse
    {
        // "image", "video" or "audio".
        public required string Kind { get; init; }
        // For images, Slack's thumbnail when the image is large enough to have one.
        // Otherwise the original when browsers can play/display it, else the
        // converted copy slackpack made (e.g. JPEG for HEIC).
        public required string Url { get; init; }
        // Dimensions of Url's image or video, null when unknown (always for
        // audio). Lets slackopy reserve space before loading.
        public int? Width { get; init; }
        public int? Height { get; init; }
        // For video, a still frame to show before it plays, null when there's none.
        public string? PosterUrl { get; init; }
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
        // The author's profile version as of this message (the earliest one
        // before that), null when we have none. Lets slackopy tell when an
        // author's identity changed, and keys their profile in Users.
        public int? AuthorProfileId { get; init; }
        public string? AuthorAvatarUrl { get; init; }
        public required string Timestamp { get; init; }
        public required FileResponse[] Files { get; init; }
        public required ReactionResponse[] Reactions { get; init; }
        public required MessageResponse[] Replies { get; init; }
    }

    public class UserResponse
    {
        public required string Id { get; init; }
        public required int ProfileId { get; init; }
        public required string DisplayName { get; init; }
        public required string RealName { get; init; }
        public string? Title { get; init; }
        // The profile's own avatar (512px), which a message's avatar can lag
        // behind or run ahead of (see UserAsOfSql).
        public string? AvatarUrl { get; init; }
    }

    public class ChannelResponse
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Topic { get; init; }
        // The channel's description.
        public string? Purpose { get; init; }
        public required string[] PreviousNames { get; init; }
        public required string Created { get; init; }
        // Keys the creator's profile (as of creation) in Users, null when we have none.
        public int? CreatorProfileId { get; init; }
        public required bool IsArchived { get; init; }
        // When it was archived, null when unknown (Slack only records it as a
        // message in the channel, which may be missing).
        public string? Archived { get; init; }
        // Keys the archiver's profile (as of archiving) in Users.
        public int? ArchiverProfileId { get; init; }
    }

    public required ChannelResponse Channel { get; init; }
    public required MessageResponse[] Messages { get; init; }
    // Message authors, users mentioned in text and the channel's creator and
    // archiver, by profile id.
    public required Dictionary<int, UserResponse> Users { get; init; }
}
