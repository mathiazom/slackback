using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using FastEndpoints;
using Npgsql;

namespace Api.Routes.GetChannel;

public class GetChannel(NpgsqlDataSource db, SeaweedFsOptions seaweedFs) : EndpointWithoutRequest<GetChannelResponse>
{
    public override void Configure()
    {
        Get("/channels/{ChannelId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var channelId = Route<string>("ChannelId", isRequired: true)!;

        await using var conn = await db.OpenConnectionAsync(ct);

        var channel = await conn.QuerySingleOrDefaultAsync<ChannelRow>(
            new CommandDefinition(
                """
                SELECT
                    public_id AS Id,
                    data->>'name' AS Name,
                    data->'topic'->>'value' AS Topic
                FROM channel
                WHERE public_id = @ChannelId
                ORDER BY timestamp DESC
                LIMIT 1
                """,
                new { ChannelId = channelId },
                cancellationToken: ct
            )
        );

        if (channel is null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        var messageRows = await conn.QueryAsync<MessageRow>(
            new CommandDefinition(
                $"""
                SELECT
                    m.public_id AS Id,
                    m.data->>'ts' AS Ts,
                    m.thread_ts AS ThreadTs,
                    m.data->>'text' AS Text,
                    m.data->>'user' AS AuthorId,
                    u.snapshot_id AS AuthorSnapshotId,
                    COALESCE(u.display_name, u.real_name, u.name, m.data->>'user') AS AuthorDisplayName,
                    COALESCE(u.real_name, u.name, m.data->>'user') AS AuthorRealName,
                    u.avatar_72_id AS AuthorAvatarFileId,
                    u.avatar_512_id AS AuthorAvatarLargeFileId,
                    (m.data->'files')::text AS FilesJson,
                    (m.data->'reactions')::text AS ReactionsJson
                FROM message m
                JOIN channel c ON c.id = m.channel_id
                LEFT JOIN LATERAL (
                    {UserAsOfSql("m.data->>'user'", "m.data->>'ts'")}
                ) u ON true
                WHERE c.public_id = @ChannelId
                ORDER BY COALESCE(m.thread_ts, m.public_id) ASC, m.public_id ASC
                """,
                new { ChannelId = channelId },
                cancellationToken: ct
            )
        );

        // Topic can carry mentions/emoji too, same as any message - scanned
        // alongside message text so its own references get resolved even when
        // they don't otherwise appear anywhere in the channel's messages.
        var allTexts = messageRows
            .Select(row => row.Text)
            .Append(channel.Topic)
            .Where(text => text is not null)
            .Select(text => text!)
            .ToArray();

        // Parsed once up front so reactors and reaction emoji can join the same
        // batched user/emoji lookups as mentions in text.
        var reactionsByMessageId = messageRows.ToDictionary(
            row => row.Id,
            row => row.ReactionsJson is not null
                ? JsonSerializer.Deserialize<ReactionData[]>(row.ReactionsJson, ReactionJsonOptions) ?? []
                : []
        );
        var allReactions = reactionsByMessageId.Values.SelectMany(reactions => reactions).ToArray();

        // The topic belongs to the latest channel snapshot, so its mentions
        // resolve as of now rather than any message's time.
        var topicTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        // Batched instead of resolved per-message: collect every mentioned user
        // across all messages/replies up front, then one query for all of them,
        // rather than a lookup per mention occurrence. Users resolve as of the
        // message they appear in (reactors too - a reaction's own time isn't
        // known), so each (user, message ts) pair is its own lookup.
        var mentionedUsers = messageRows
            .SelectMany(row => (row.Text is not null ? UserMentionRegex.Matches(row.Text).Select(m => m.Groups[1].Value) : [])
                .Concat(reactionsByMessageId[row.Id].SelectMany(reaction => reaction.Users ?? []))
                .Select(userId => (UserId: userId, Ts: row.Ts)))
            .Concat(channel.Topic is not null
                ? UserMentionRegex.Matches(channel.Topic).Select(m => (UserId: m.Groups[1].Value, Ts: topicTs))
                : [])
            .Distinct()
            .ToArray();

        var userNames = mentionedUsers.Length == 0
            ? new Dictionary<(string UserId, string Ts), UserNamesRow>()
            : (await conn.QueryAsync<UserNamesRow>(
                new CommandDefinition(
                    // Same name fallbacks as message authors: mentions use the
                    // display name, reactors the display name (with real name).
                    $"""
                    SELECT
                        r.user_id AS Id,
                        r.ts AS Ts,
                        COALESCE(u.display_name, u.real_name, u.name) AS DisplayName,
                        COALESCE(u.real_name, u.name) AS RealName
                    FROM unnest(@UserIds, @Tss) AS r(user_id, ts)
                    JOIN LATERAL (
                        {UserAsOfSql("r.user_id", "r.ts")}
                    ) u ON true
                    """,
                    new { UserIds = mentionedUsers.Select(user => user.UserId).ToArray(), Tss = mentionedUsers.Select(user => user.Ts).ToArray() },
                    cancellationToken: ct
                )
            )).ToDictionary(row => (row.Id, row.Ts));

        // Same batching as user mentions. Only channels we've archived resolve,
        // which is also what decides whether slackopy can link to them.
        var mentionedChannelIds = allTexts
            .SelectMany(text => ChannelMentionRegex.Matches(text).Select(m => m.Groups[1].Value))
            .Distinct()
            .ToArray();

        var channelNames = mentionedChannelIds.Length == 0
            ? new Dictionary<string, string>()
            : (await conn.QueryAsync<(string Id, string Name)>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT ON (public_id)
                        public_id AS Id,
                        data->>'name' AS Name
                    FROM channel
                    WHERE public_id = ANY(@ChannelIds)
                    ORDER BY public_id, timestamp DESC
                    """,
                    new { ChannelIds = mentionedChannelIds },
                    cancellationToken: ct
                )
            )).ToDictionary(row => row.Id, row => row.Name);

        // Same batching approach as mentions: only custom (workspace) emoji need
        // resolving here - standard emoji are already real Unicode characters in
        // Slack's stored text, not shortcodes, so they need no handling at all.
        var mentionedEmojiNames = allTexts
            .SelectMany(text => EmojiShortcodeRegex.Matches(text).Select(m => m.Groups[1].Value))
            .Concat(allReactions.Select(reaction => ReactionBaseName(reaction.Name)))
            .Distinct()
            .ToArray();

        var emojiFileIds = mentionedEmojiNames.Length == 0
            ? new Dictionary<string, string>()
            : (await conn.QueryAsync<(string Id, string FileId)>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT ON (public_id)
                        public_id AS Id,
                        file_id AS FileId
                    FROM emoji
                    WHERE public_id = ANY(@EmojiNames)
                    ORDER BY public_id, timestamp DESC
                    """,
                    new { EmojiNames = mentionedEmojiNames },
                    cancellationToken: ct
                )
            )).ToDictionary(row => row.Id, row => row.FileId);

        var messages = messageRows
            .GroupBy(row => row.ThreadTs ?? row.Id)
            .Select(group =>
            {
                var rows = group.ToList();
                var replies = rows.Skip(1).Select(row => MapMessage(row, reactionsByMessageId[row.Id], [], userNames, channelNames, emojiFileIds, seaweedFs.PublicUrl));
                return MapMessage(rows[0], reactionsByMessageId[rows[0].Id], replies, userNames, channelNames, emojiFileIds, seaweedFs.PublicUrl);
            })
            .ToArray();

        await SendAsync(
            new GetChannelResponse
            {
                Channel = new GetChannelResponse.ChannelResponse
                {
                    Id = channel.Id,
                    Name = channel.Name,
                    Topic = channel.Topic is not null
                        ? ResolveMentions(ResolveEmoji(channel.Topic, emojiFileIds, seaweedFs.PublicUrl), topicTs, userNames, channelNames)
                        : null
                },
                Messages = messages
            },
            cancellation: ct
        );
    }

    // Slack's own U/W-prefixed user ids; the optional |label is a name Slack
    // sometimes inlines, only used when the user isn't in the DB.
    private static readonly Regex UserMentionRegex = new(@"<@([UW][A-Z0-9]+)(?:\|([^>]*))?>", RegexOptions.Compiled);
    // Slack doesn't always inline the channel name (e.g. "<#C028FFPT9K3>"), and
    // when it does it's the name at posting time, not the current one.
    private static readonly Regex ChannelMentionRegex = new(@"<#([CG][A-Z0-9]+)(?:\|([^>]*))?>", RegexOptions.Compiled);
    // Slack shortcode names: lowercase letters, digits, underscore, hyphen, plus.
    private static readonly Regex EmojiShortcodeRegex = new(@":([a-z0-9_+-]+):", RegexOptions.Compiled);

    // Mentions stay in Slack's own <@id|name>/<#id|name> syntax, just with the
    // name filled in, so slackopy can style them - like emoji, slackback never
    // emits HTML. @here/@channel/@everyone need no lookup and pass through as-is.
    // A channel we haven't archived becomes plain "#name" text instead, since
    // slackopy links every <#id|name> and there'd be nothing to link to.
    // Runs after emoji resolution so a ":word:" inside a looked-up name can't be
    // turned into an emoji token nested inside the mention token.
    private static string ResolveMentions(string text, string ts, IReadOnlyDictionary<(string UserId, string Ts), UserNamesRow> userNames, IReadOnlyDictionary<string, string> channelNames)
    {
        text = UserMentionRegex.Replace(text, match =>
        {
            var inlineName = match.Groups[2].Success && match.Groups[2].Value != "" ? match.Groups[2].Value : null;
            var userId = match.Groups[1].Value;
            // Display name, so a mention reads the same as that user's messages.
            var name = userNames.TryGetValue((userId, ts), out var resolved) ? EscapeSlackText(resolved.DisplayName) : inlineName ?? userId;
            return $"<@{userId}|{name}>";
        });

        return ChannelMentionRegex.Replace(text, match =>
        {
            var channelId = match.Groups[1].Value;
            if (channelNames.TryGetValue(channelId, out var name))
            {
                return $"<#{channelId}|{EscapeSlackText(name)}>";
            }
            var inlineName = match.Groups[2].Success && match.Groups[2].Value != "" ? match.Groups[2].Value : channelId;
            return $"#{inlineName}";
        });
    }

    // Names from the DB are raw, unlike inline labels, which Slack has already
    // escaped. Without this a ">" in a real name would end the mention token early.
    private static string EscapeSlackText(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Emoji resolve to a dedicated <emoji:url|name> token (not a real <img> tag -
    // slackback never emits HTML) that slackopy's own renderer recognizes and
    // turns into an image, the same place all other markup becomes HTML.
    // Unarchived/unknown shortcodes are left as literal ":name:" text.
    private static string ResolveEmoji(string text, IReadOnlyDictionary<string, string> emojiFileIds, string seaweedFsPublicUrl)
    {
        return EmojiShortcodeRegex.Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            return emojiFileIds.TryGetValue(name, out var fileId)
                ? $"<emoji:{seaweedFsPublicUrl}/{fileId}|{name}>"
                : match.Value;
        });
    }

    private static readonly JsonSerializerOptions ReactionJsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Skin-toned reactions are stored as e.g. "+1::skin-tone-3". Custom emoji
    // never carry a tone, so only the base name is looked up in the emoji table;
    // the full name is kept for slackopy's standard-emoji lookup.
    private static string ReactionBaseName(string name) => name.Split("::")[0];

    private static GetChannelResponse.MessageResponse MapMessage(MessageRow row, ReactionData[] reactions, IEnumerable<GetChannelResponse.MessageResponse> replies, IReadOnlyDictionary<(string UserId, string Ts), UserNamesRow> userNames, IReadOnlyDictionary<string, string> channelNames, IReadOnlyDictionary<string, string> emojiFileIds, string seaweedFsPublicUrl)
    {
        var files = new List<GetChannelResponse.FileResponse>();

        var filesElement = row.FilesJson is not null ? JsonDocument.Parse(row.FilesJson).RootElement : default;

        if (filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                if (!file.TryGetProperty("archived_file_id", out var archivedFileIdProp))
                {
                    continue;
                }

                var archivedFileId = archivedFileIdProp.GetString();
                if (string.IsNullOrEmpty(archivedFileId))
                {
                    continue;
                }

                files.Add(new GetChannelResponse.FileResponse
                {
                    Id = file.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? archivedFileId : archivedFileId,
                    Name = file.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null,
                    Mimetype = file.TryGetProperty("mimetype", out var mimetypeProp) ? mimetypeProp.GetString() : null,
                    Url = $"{seaweedFsPublicUrl}/{archivedFileId}"
                });
            }
        }

        return new GetChannelResponse.MessageResponse
        {
            Id = row.Id,
            Text = row.Text is not null ? ResolveMentions(ResolveEmoji(row.Text, emojiFileIds, seaweedFsPublicUrl), row.Ts, userNames, channelNames) : null,
            AuthorDisplayName = row.AuthorDisplayName ?? "Unknown",
            AuthorRealName = row.AuthorRealName ?? "Unknown",
            AuthorId = row.AuthorId,
            AuthorSnapshotId = row.AuthorSnapshotId,
            AuthorAvatarUrl = row.AuthorAvatarFileId is not null ? $"{seaweedFsPublicUrl}/{row.AuthorAvatarFileId}" : null,
            AuthorAvatarLargeUrl = row.AuthorAvatarLargeFileId is not null ? $"{seaweedFsPublicUrl}/{row.AuthorAvatarLargeFileId}" : null,
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(row.Ts.Split(".")[0])).ToString("o"),
            Files = [.. files],
            Reactions = [.. reactions.Select(reaction => new GetChannelResponse.ReactionResponse
            {
                Name = reaction.Name,
                ImageUrl = emojiFileIds.TryGetValue(ReactionBaseName(reaction.Name), out var fileId)
                    ? $"{seaweedFsPublicUrl}/{fileId}"
                    : null,
                Count = reaction.Count,
                Users = [.. (reaction.Users ?? []).Select(userId => userNames.TryGetValue((userId, row.Ts), out var names)
                    ? new GetChannelResponse.ReactionUserResponse { DisplayName = names.DisplayName, RealName = names.RealName }
                    : new GetChannelResponse.ReactionUserResponse { DisplayName = userId, RealName = userId })]
            })],
            Replies = [.. replies]
        };
    }

    // The user snapshot valid at a Slack ts (latest snapshot taken at or before it),
    // for use inside a LATERAL join. Before a user's first snapshot (pre-archive
    // history) names fall back to the earliest snapshot, but the avatar stays null
    // since we have no record of what they looked like then. snapshot_id is null
    // in that case too, so "same identity" comparisons treat all pre-history alike.
    // Snapshot timestamps are naive pack-time CURRENT_TIMESTAMPs, so the ts is
    // converted the same way (session time zone) before comparing.
    private static string UserAsOfSql(string userIdSql, string tsSql) =>
        $"""
        SELECT
            as_of.id AS snapshot_id,
            COALESCE(as_of.data, earliest.data)->'profile'->>'display_name' AS display_name,
            COALESCE(as_of.data, earliest.data)->>'real_name' AS real_name,
            COALESCE(as_of.data, earliest.data)->>'name' AS name,
            as_of.data->'profile'->>'archived_avatar_72_id' AS avatar_72_id,
            as_of.data->'profile'->>'archived_avatar_512_id' AS avatar_512_id
        FROM (SELECT 1) one
        LEFT JOIN LATERAL (
            SELECT id, data
            FROM "user"
            WHERE public_id = {userIdSql}
              AND timestamp <= to_timestamp(({tsSql})::double precision)::timestamp
            ORDER BY timestamp DESC
            LIMIT 1
        ) as_of ON true
        LEFT JOIN LATERAL (
            SELECT data
            FROM "user"
            WHERE public_id = {userIdSql}
              AND as_of.id IS NULL
            ORDER BY timestamp ASC
            LIMIT 1
        ) earliest ON true
        WHERE as_of.id IS NOT NULL OR earliest.data IS NOT NULL
        """;

    private record ChannelRow(string Id, string Name, string? Topic);

    private record UserNamesRow(string Id, string Ts, string DisplayName, string RealName);

    private record MessageRow(string Id, string Ts, string? ThreadTs, string? Text, string? AuthorId, int? AuthorSnapshotId, string? AuthorDisplayName, string? AuthorRealName, string? AuthorAvatarFileId, string? AuthorAvatarLargeFileId, string? FilesJson, string? ReactionsJson);

    // Slack's own reaction shape. Count is authoritative: Slack can truncate the
    // users list on heavily-reacted messages.
    private record ReactionData(string Name, int Count, string[]? Users);
}
