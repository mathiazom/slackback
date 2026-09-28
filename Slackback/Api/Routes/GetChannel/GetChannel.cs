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
                """
                SELECT
                    m.public_id AS Id,
                    m.data->>'ts' AS Ts,
                    m.thread_ts AS ThreadTs,
                    m.data->>'text' AS Text,
                    COALESCE(u.data->'profile'->>'display_name', u.data->>'real_name', u.data->>'name', m.data->>'user') AS AuthorDisplayName,
                    COALESCE(u.data->>'real_name', u.data->>'name', m.data->>'user') AS AuthorRealName,
                    (m.data->'files')::text AS FilesJson
                FROM message m
                JOIN channel c ON c.id = m.channel_id
                LEFT JOIN LATERAL (
                    SELECT data
                    FROM "user"
                    WHERE public_id = m.data->>'user'
                    ORDER BY timestamp DESC
                    LIMIT 1
                ) u ON true
                WHERE c.public_id = @ChannelId
                ORDER BY COALESCE(m.thread_ts, m.public_id), m.public_id
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

        // Batched instead of resolved per-message: collect every mentioned user id
        // across all messages/replies up front, then one query for all of them,
        // rather than a lookup per mention occurrence.
        var mentionedUserIds = allTexts
            .SelectMany(text => UserMentionRegex.Matches(text).Select(m => m.Groups[1].Value))
            .Distinct()
            .ToArray();

        var userNames = mentionedUserIds.Length == 0
            ? new Dictionary<string, string>()
            : (await conn.QueryAsync<(string Id, string Name)>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT ON (public_id)
                        public_id AS Id,
                        COALESCE(data->>'real_name', data->>'name') AS Name
                    FROM "user"
                    WHERE public_id = ANY(@UserIds)
                    ORDER BY public_id, timestamp DESC
                    """,
                    new { UserIds = mentionedUserIds },
                    cancellationToken: ct
                )
            )).ToDictionary(row => row.Id, row => row.Name);

        // Same batching approach as mentions: only custom (workspace) emoji need
        // resolving here - standard emoji are already real Unicode characters in
        // Slack's stored text, not shortcodes, so they need no handling at all.
        var mentionedEmojiNames = allTexts
            .SelectMany(text => EmojiShortcodeRegex.Matches(text).Select(m => m.Groups[1].Value))
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
                var replies = rows.Skip(1).Select(row => MapMessage(row, [], userNames, emojiFileIds, seaweedFs.PublicUrl));
                return MapMessage(rows[0], replies, userNames, emojiFileIds, seaweedFs.PublicUrl);
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
                        ? ResolveEmoji(ResolveMentions(channel.Topic, userNames), emojiFileIds, seaweedFs.PublicUrl)
                        : null
                },
                Messages = messages
            },
            cancellation: ct
        );
    }

    // Slack's own U/W-prefixed user ids; the optional |label is a fallback name
    // Slack sometimes inlines, which takes priority over a DB lookup when present.
    private static readonly Regex UserMentionRegex = new(@"<@([UW][A-Z0-9]+)(?:\|([^>]*))?>", RegexOptions.Compiled);
    // Channel mentions always carry their display name inline, so no DB lookup needed.
    private static readonly Regex ChannelMentionRegex = new(@"<#[A-Z0-9]+\|([^>]*)>", RegexOptions.Compiled);
    private static readonly Regex SpecialMentionRegex = new(@"<!(here|channel|everyone)>", RegexOptions.Compiled);
    // Slack shortcode names: lowercase letters, digits, underscore, hyphen, plus.
    private static readonly Regex EmojiShortcodeRegex = new(@":([a-z0-9_+-]+):", RegexOptions.Compiled);

    private static string ResolveMentions(string text, IReadOnlyDictionary<string, string> userNames)
    {
        text = UserMentionRegex.Replace(text, match =>
        {
            var inlineName = match.Groups[2].Success ? match.Groups[2].Value : null;
            var userId = match.Groups[1].Value;
            var name = inlineName ?? (userNames.TryGetValue(userId, out var resolved) ? resolved : userId);
            return $"@{name}";
        });

        text = ChannelMentionRegex.Replace(text, match => $"#{match.Groups[1].Value}");

        return SpecialMentionRegex.Replace(text, match => $"@{match.Groups[1].Value}");
    }

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

    private static GetChannelResponse.MessageResponse MapMessage(MessageRow row, IEnumerable<GetChannelResponse.MessageResponse> replies, IReadOnlyDictionary<string, string> userNames, IReadOnlyDictionary<string, string> emojiFileIds, string seaweedFsPublicUrl)
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
            Text = row.Text is not null ? ResolveEmoji(ResolveMentions(row.Text, userNames), emojiFileIds, seaweedFsPublicUrl) : null,
            AuthorDisplayName = row.AuthorDisplayName ?? "Unknown",
            AuthorRealName = row.AuthorRealName ?? "Unknown",
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(row.Ts.Split(".")[0])).ToString("o"),
            Files = [.. files],
            Replies = [.. replies]
        };
    }

    private record ChannelRow(string Id, string Name, string? Topic);

    private record MessageRow(string Id, string Ts, string? ThreadTs, string? Text, string? AuthorDisplayName,string? AuthorRealName, string? FilesJson);
}
