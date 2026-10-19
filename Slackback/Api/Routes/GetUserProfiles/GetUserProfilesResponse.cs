namespace Api.Routes.GetUserProfiles;

public class GetUserProfilesResponse
{
    public class ProfileResponse
    {
        // Slack user id.
        public required string Id { get; init; }
        public required int ProfileId { get; init; }
        public required string DisplayName { get; init; }
        public required string RealName { get; init; }
        // Slack username (the @handle).
        public string? Name { get; init; }
        public string? Title { get; init; }
        public string? Pronouns { get; init; }
        public string? Email { get; init; }
        public string? Phone { get; init; }
        public required bool IsPrimaryOwner { get; init; }
        public required bool IsOwner { get; init; }
        public required bool IsAdmin { get; init; }
        // Guest, and single-channel guest.
        public required bool IsRestricted { get; init; }
        public required bool IsUltraRestricted { get; init; }
        public required bool IsBot { get; init; }
        // Deactivated.
        public required bool IsDeleted { get; init; }
        // 72px and 512px.
        public string? AvatarUrl { get; init; }
        public string? AvatarLargeUrl { get; init; }
        // When the profile changed to this in Slack, and away from it (null for
        // the current one).
        public required string Since { get; init; }
        public string? Until { get; init; }
    }

    public required ProfileResponse[] Profiles { get; init; }
}
