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
                    NULLIF(data->'topic'->>'value', '') AS Topic,
                    NULLIF(data->'purpose'->>'value', '') AS Purpose,
                    (data->>'created')::bigint AS Created,
                    NULLIF(data->>'creator', '') AS CreatorId,
                    COALESCE((data->>'is_archived')::boolean, false) AS IsArchived,
                    (data->'previous_names')::text AS PreviousNamesJson
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

        // Slack keeps no archive time on the channel itself, only the system
        // message posted when it happens. A later unarchive means the channel was
        // archived again without one, so then the time is unknown.
        var archiveEvent = channel.IsArchived
            ? await conn.QuerySingleOrDefaultAsync<ArchiveEventRow>(
                new CommandDefinition(
                    """
                    SELECT
                        m.data->>'subtype' AS Subtype,
                        m.data->>'ts' AS Ts,
                        NULLIF(m.data->>'user', '') AS UserId
                    FROM message m
                    JOIN channel c ON c.id = m.channel_id
                    WHERE c.public_id = @ChannelId
                      AND m.data->>'subtype' IN ('channel_archive', 'channel_unarchive')
                    ORDER BY (m.data->>'ts')::numeric DESC
                    LIMIT 1
                    """,
                    new { ChannelId = channelId },
                    cancellationToken: ct
                )
            )
            : null;
        var archived = archiveEvent?.Subtype == "channel_archive" ? archiveEvent : null;

        var messageRows = await conn.QueryAsync<MessageRow>(
            new CommandDefinition(
                $"""
                SELECT
                    m.public_id AS Id,
                    m.data->>'ts' AS Ts,
                    m.thread_ts AS ThreadTs,
                    m.data->>'text' AS Text,
                    m.data->>'user' AS AuthorId,
                    u.profile_id AS AuthorProfileId,
                    COALESCE(u.display_name, u.real_name, u.name, m.data->>'user') AS AuthorDisplayName,
                    COALESCE(u.real_name, u.name, m.data->>'user') AS AuthorRealName,
                    u.title AS AuthorTitle,
                    u.avatar_72_id AS AuthorAvatarFileId,
                    u.profile_avatar_512_id AS AuthorProfileAvatarFileId,
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
            .Append(channel.Purpose)
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

        // The topic and purpose belong to the latest channel snapshot, so their
        // mentions resolve as of now rather than any message's time.
        var topicTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        // Batched instead of resolved per-message: collect every mentioned user
        // across all messages/replies up front, then one query for all of them,
        // rather than a lookup per mention occurrence. Users resolve as of the
        // message they appear in (reactors too - a reaction's own time isn't
        // known), so each (user, message ts) pair is its own lookup.
        // Mentions in text are kept apart from reactors, since only they get a
        // profile popover.
        var textMentions = messageRows
            .SelectMany(row => row.Text is not null
                ? UserMentionRegex.Matches(row.Text).Select(m => (UserId: m.Groups[1].Value, Ts: row.Ts))
                : [])
            .Concat(new[] { channel.Topic, channel.Purpose }
                .Where(text => text is not null)
                .SelectMany(text => UserMentionRegex.Matches(text!).Select(m => (UserId: m.Groups[1].Value, Ts: topicTs))))
            .ToHashSet();
        // Who created and archived the channel, as of when they did.
        var creator = channel.CreatorId is not null ? (UserId: channel.CreatorId, Ts: channel.Created.ToString()) : ((string UserId, string Ts)?)null;
        var archiver = archived?.UserId is not null ? (UserId: archived.UserId, Ts: archived.Ts) : ((string UserId, string Ts)?)null;
        var channelPeople = new[] { creator, archiver }
            .Where(person => person is not null)
            .Select(person => person!.Value)
            .ToArray();
        var mentionedUsers = messageRows
            .SelectMany(row => reactionsByMessageId[row.Id]
                .SelectMany(reaction => reaction.Users ?? [])
                .Select(userId => (UserId: userId, Ts: row.Ts)))
            .Concat(textMentions)
            .Concat(channelPeople)
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
                        u.profile_id AS ProfileId,
                        COALESCE(u.display_name, u.real_name, u.name) AS DisplayName,
                        COALESCE(u.real_name, u.name) AS RealName,
                        u.title AS Title,
                        u.profile_avatar_512_id AS ProfileAvatarFileId
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

        // Everyone shown with a profile popover: message authors, mentioned
        // users and the channel's creator and archiver, each as the profile
        // version they're shown as.
        var users = messageRows
            .Where(row => row.AuthorId is not null && row.AuthorProfileId is not null)
            .Select(row => new GetChannelResponse.UserResponse
            {
                Id = row.AuthorId!,
                ProfileId = row.AuthorProfileId!.Value,
                DisplayName = row.AuthorDisplayName ?? row.AuthorId!,
                RealName = row.AuthorRealName ?? row.AuthorId!,
                Title = row.AuthorTitle,
                AvatarUrl = row.AuthorProfileAvatarFileId is not null ? $"{seaweedFs.PublicUrl}/{row.AuthorProfileAvatarFileId}" : null
            })
            .Concat(textMentions
                .Concat(channelPeople)
                .Where(userNames.ContainsKey)
                .Select(mention => userNames[mention])
                .Select(names => new GetChannelResponse.UserResponse
                {
                    Id = names.Id,
                    ProfileId = names.ProfileId,
                    DisplayName = names.DisplayName,
                    RealName = names.RealName,
                    Title = names.Title,
                    AvatarUrl = names.ProfileAvatarFileId is not null ? $"{seaweedFs.PublicUrl}/{names.ProfileAvatarFileId}" : null
                }))
            .DistinctBy(user => user.ProfileId)
            .ToDictionary(user => user.ProfileId);

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
                        : null,
                    Purpose = channel.Purpose is not null
                        ? ResolveMentions(ResolveEmoji(channel.Purpose, emojiFileIds, seaweedFs.PublicUrl), topicTs, userNames, channelNames)
                        : null,
                    // Slack keeps a group DM's own name when it's converted to a
                    // channel, which was never a channel name.
                    PreviousNames = [.. (channel.PreviousNamesJson is not null
                            ? JsonSerializer.Deserialize<string[]>(channel.PreviousNamesJson) ?? []
                            : [])
                        .Where(name => !name.StartsWith("mpdm-"))],
                    Created = TimestampFromTs(channel.Created.ToString()),
                    CreatorProfileId = creator is not null && userNames.TryGetValue(creator.Value, out var creatorNames) ? creatorNames.ProfileId : null,
                    IsArchived = channel.IsArchived,
                    Archived = archived is not null ? TimestampFromTs(archived.Ts) : null,
                    ArchiverProfileId = archiver is not null && userNames.TryGetValue(archiver.Value, out var archiverNames) ? archiverNames.ProfileId : null
                },
                Messages = messages,
                Users = users
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
    // emits HTML. A user we have a profile of becomes <@id:profileId|name>
    // instead, pointing at it in the response's users. @here/@channel/@everyone need no lookup and pass through as-is.
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
            return userNames.TryGetValue((userId, ts), out var resolved)
                ? $"<@{userId}:{resolved.ProfileId}|{EscapeSlackText(resolved.DisplayName)}>"
                : $"<@{userId}|{inlineName ?? userId}>";
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

    // Image types every major browser displays inline. Others (HEIC, TIFF, ...)
    // only show inline through a converted copy.
    private static readonly HashSet<string> BrowserImageMimetypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/avif", "image/bmp", "image/svg+xml"
    };

    // Video containers browsers play; whether they can decode the codec (HEVC
    // isn't everywhere) only the browser knows, so slackopy always links the file too.
    private static readonly HashSet<string> BrowserVideoMimetypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4", "video/quicktime", "video/webm"
    };

    private static readonly HashSet<string> BrowserAudioMimetypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mpeg", "audio/mp4", "audio/x-m4a", "audio/aac", "audio/wav", "audio/x-wav", "audio/ogg", "audio/webm", "audio/flac"
    };

    private static GetChannelResponse.EmbedResponse? MapEmbed(JsonElement file, string? mimetype, string url, string seaweedFsPublicUrl)
    {
        // slackpack archives Slack's thumbnail of large images, so they load fast.
        var thumbFileId = file.TryGetProperty("archived_thumb_file_id", out var thumbFileIdProp) ? thumbFileIdProp.GetString() : null;
        if (!string.IsNullOrEmpty(thumbFileId))
        {
            return new GetChannelResponse.EmbedResponse
            {
                Kind = "image",
                Url = $"{seaweedFsPublicUrl}/{thumbFileId}",
                Width = PositiveInt(file, "archived_thumb_w"),
                Height = PositiveInt(file, "archived_thumb_h")
            };
        }
        var convertedFileId = file.TryGetProperty("archived_converted_file_id", out var convertedFileIdProp) ? convertedFileIdProp.GetString() : null;
        if (!string.IsNullOrEmpty(convertedFileId))
        {
            return new GetChannelResponse.EmbedResponse
            {
                Kind = "image",
                Url = $"{seaweedFsPublicUrl}/{convertedFileId}",
                Width = PositiveInt(file, "archived_converted_w"),
                Height = PositiveInt(file, "archived_converted_h")
            };
        }
        if (mimetype is null)
        {
            return null;
        }
        if (BrowserImageMimetypes.Contains(mimetype))
        {
            return new GetChannelResponse.EmbedResponse
            {
                Kind = "image",
                Url = url,
                Width = PositiveInt(file, "original_w"),
                Height = PositiveInt(file, "original_h")
            };
        }
        if (BrowserVideoMimetypes.Contains(mimetype))
        {
            // Slack gives no video dimensions; slackpack probes them.
            var posterFileId = file.TryGetProperty("archived_poster_file_id", out var posterFileIdProp) ? posterFileIdProp.GetString() : null;
            return new GetChannelResponse.EmbedResponse
            {
                Kind = "video",
                Url = url,
                Width = PositiveInt(file, "archived_video_w"),
                Height = PositiveInt(file, "archived_video_h"),
                PosterUrl = !string.IsNullOrEmpty(posterFileId) ? $"{seaweedFsPublicUrl}/{posterFileId}" : null
            };
        }
        if (BrowserAudioMimetypes.Contains(mimetype))
        {
            return new GetChannelResponse.EmbedResponse { Kind = "audio", Url = url };
        }
        return null;
    }

    // Slack stores unknown dimensions as 0.
    private static int? PositiveInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value) && value > 0
            ? value
            : null;

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

                var mimetype = file.TryGetProperty("mimetype", out var mimetypeProp) ? mimetypeProp.GetString() : null;
                var url = $"{seaweedFsPublicUrl}/{archivedFileId}";

                files.Add(new GetChannelResponse.FileResponse
                {
                    Id = file.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? archivedFileId : archivedFileId,
                    Name = file.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null,
                    Mimetype = mimetype,
                    Url = url,
                    Embed = MapEmbed(file, mimetype, url, seaweedFsPublicUrl)
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
            AuthorProfileId = row.AuthorProfileId,
            AuthorAvatarUrl = row.AuthorAvatarFileId is not null ? $"{seaweedFsPublicUrl}/{row.AuthorAvatarFileId}" : null,
            Timestamp = TimestampFromTs(row.Ts),
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

    // The user's profile version (see UserProfiles) at a Slack ts, from the latest
    // snapshot taken at or before it, for use inside a LATERAL join. Before a
    // user's first snapshot (pre-archive history) it falls back to the earliest
    // snapshot, the closest record we have.
    // Snapshot timestamps are naive pack-time CURRENT_TIMESTAMPs, so the ts is
    // converted the same way (session time zone) before comparing.
    // The avatar resolves separately, from the latest snapshot whose avatar was
    // already current at the ts (slackpack's archived_avatar_since, usually earlier
    // than the snapshot itself), so it can change ahead of the names and reach into
    // pre-archive history. Before any of them it stays null, since we have no
    // record of what they looked like then. The profile's own avatar is there
    // too, for its popover.
    private static string UserAsOfSql(string userIdSql, string tsSql) =>
        $"""
        SELECT
            snapshot.profile_id,
            snapshot.fields->>'display_name' AS display_name,
            snapshot.fields->>'real_name' AS real_name,
            snapshot.fields->>'name' AS name,
            snapshot.fields->>'title' AS title,
            snapshot.fields->>'avatar_512_id' AS profile_avatar_512_id,
            avatar.data->'profile'->>'archived_avatar_72_id' AS avatar_72_id
        FROM (SELECT 1) one
        LEFT JOIN LATERAL (
            SELECT id
            FROM "user"
            WHERE public_id = {userIdSql}
              AND timestamp <= to_timestamp(({tsSql})::double precision)::timestamp
            ORDER BY timestamp DESC
            LIMIT 1
        ) as_of ON true
        LEFT JOIN LATERAL (
            SELECT id
            FROM "user"
            WHERE public_id = {userIdSql}
              AND as_of.id IS NULL
            ORDER BY timestamp ASC
            LIMIT 1
        ) earliest ON true
        JOIN LATERAL (
            {UserProfiles.SnapshotsSql($"public_id = {userIdSql}")}
        ) snapshot ON snapshot.id = COALESCE(as_of.id, earliest.id)
        LEFT JOIN LATERAL (
            SELECT data
            FROM "user"
            WHERE public_id = {userIdSql}
              AND (data->'profile'->>'archived_avatar_since')::bigint <= ({tsSql})::double precision
            ORDER BY timestamp DESC
            LIMIT 1
        ) avatar ON true
        """;

    // A Slack ts (or unix time) as ISO 8601.
    private static string TimestampFromTs(string ts) =>
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(ts.Split(".")[0])).ToString("o");

    private record ChannelRow(string Id, string Name, string? Topic, string? Purpose, long Created, string? CreatorId, bool IsArchived, string? PreviousNamesJson);

    private record ArchiveEventRow(string Subtype, string Ts, string? UserId);

    private record UserNamesRow(string Id, string Ts, int ProfileId, string DisplayName, string RealName, string? Title, string? ProfileAvatarFileId);

    private record MessageRow(string Id, string Ts, string? ThreadTs, string? Text, string? AuthorId, int? AuthorProfileId, string? AuthorDisplayName, string? AuthorRealName, string? AuthorTitle, string? AuthorAvatarFileId, string? AuthorProfileAvatarFileId, string? FilesJson, string? ReactionsJson);

    // Slack's own reaction shape. Count is authoritative: Slack can truncate the
    // users list on heavily-reacted messages.
    private record ReactionData(string Name, int Count, string[]? Users);
}
