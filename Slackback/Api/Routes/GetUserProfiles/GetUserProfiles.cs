using Dapper;
using FastEndpoints;
using Npgsql;

namespace Api.Routes.GetUserProfiles;

// Every profile version of every user (see UserProfiles): messages show their
// author as of the message, so that's the profile they link to.
public class GetUserProfiles(NpgsqlDataSource db, SeaweedFsOptions seaweedFs) : EndpointWithoutRequest<GetUserProfilesResponse>
{
    public override void Configure()
    {
        Get("/user-profiles");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);

        // Dated by Slack's own last-change time on each version's first snapshot,
        // not the snapshot's: slackpack only notices a change at its next sync.
        // A hidden field changing in between moves that later, which still holds
        // as "looked like this since". On a user's first version it's the last
        // change before the archive started.
        var rows = await conn.QueryAsync<ProfileRow>(
            new CommandDefinition(
                $"""
                SELECT
                    public_id AS Id,
                    profile_id AS ProfileId,
                    fields->>'name' AS Name,
                    fields->>'display_name' AS DisplayName,
                    fields->>'real_name' AS RealName,
                    fields->>'title' AS Title,
                    fields->>'pronouns' AS Pronouns,
                    fields->>'email' AS Email,
                    fields->>'phone' AS Phone,
                    (fields->>'is_primary_owner')::boolean AS IsPrimaryOwner,
                    (fields->>'is_owner')::boolean AS IsOwner,
                    (fields->>'is_admin')::boolean AS IsAdmin,
                    (fields->>'is_restricted')::boolean AS IsRestricted,
                    (fields->>'is_ultra_restricted')::boolean AS IsUltraRestricted,
                    (fields->>'is_bot')::boolean AS IsBot,
                    (fields->>'deleted')::boolean AS IsDeleted,
                    fields->>'avatar_512_id' AS AvatarFileId,
                    changed AS Since,
                    lead(changed) OVER (PARTITION BY public_id ORDER BY timestamp) AS Until
                FROM (
                    SELECT *, COALESCE((data->>'updated')::numeric::bigint, extract(epoch FROM timestamp::timestamptz)::bigint) AS changed
                    FROM ({UserProfiles.SnapshotsSql("true")}) snapshot
                    WHERE starts
                ) version
                ORDER BY public_id, timestamp
                """,
                cancellationToken: ct
            )
        );

        await SendAsync(
            new GetUserProfilesResponse
            {
                Profiles = [.. rows.Select(row => new GetUserProfilesResponse.ProfileResponse
                {
                    Id = row.Id,
                    ProfileId = row.ProfileId,
                    // Same name fallbacks as message authors.
                    DisplayName = row.DisplayName ?? row.RealName ?? row.Name ?? row.Id,
                    RealName = row.RealName ?? row.Name ?? row.Id,
                    Name = row.Name,
                    Title = row.Title,
                    Pronouns = row.Pronouns,
                    Email = row.Email,
                    Phone = row.Phone,
                    IsPrimaryOwner = row.IsPrimaryOwner,
                    IsOwner = row.IsOwner,
                    IsAdmin = row.IsAdmin,
                    IsRestricted = row.IsRestricted,
                    IsUltraRestricted = row.IsUltraRestricted,
                    IsBot = row.IsBot,
                    IsDeleted = row.IsDeleted,
                    AvatarUrl = row.AvatarFileId is not null ? $"{seaweedFs.PublicUrl}/{row.AvatarFileId}" : null,
                    Since = DateTimeOffset.FromUnixTimeSeconds(row.Since).ToString("o"),
                    Until = row.Until is { } until ? DateTimeOffset.FromUnixTimeSeconds(until).ToString("o") : null
                })]
            },
            cancellation: ct
        );
    }

    private record ProfileRow(string Id, int ProfileId, string? Name, string? DisplayName, string? RealName, string? Title, string? Pronouns, string? Email, string? Phone, bool IsPrimaryOwner, bool IsOwner, bool IsAdmin, bool IsRestricted, bool IsUltraRestricted, bool IsBot, bool IsDeleted, string? AvatarFileId, long Since, long? Until);
}
