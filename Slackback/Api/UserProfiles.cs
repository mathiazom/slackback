namespace Api;

// A user's profile versions. slackpack keeps a snapshot for every change Slack
// reports, including ones a profile doesn't show (time zone, language, ...), so
// consecutive snapshots that show the same are one version, named by its first
// snapshot's id. Messages show their author as the version of their time.
public static class UserProfiles
{
    // What a profile shows, from a snapshot's data: the only fields a new version
    // starts on. Slack stores missing names and profile fields as "", which count
    // as missing. The avatar is the archived copies, 72px and 512px.
    public static string FieldsSql(string dataSql) =>
        $"""
        jsonb_build_object(
            'name', NULLIF({dataSql}->>'name', ''),
            'display_name', NULLIF({dataSql}->'profile'->>'display_name', ''),
            'real_name', COALESCE(NULLIF({dataSql}->>'real_name', ''), NULLIF({dataSql}->'profile'->>'real_name', '')),
            'title', NULLIF({dataSql}->'profile'->>'title', ''),
            'pronouns', NULLIF({dataSql}->'profile'->>'pronouns', ''),
            'email', NULLIF({dataSql}->'profile'->>'email', ''),
            'phone', NULLIF({dataSql}->'profile'->>'phone', ''),
            'is_primary_owner', COALESCE(({dataSql}->>'is_primary_owner')::boolean, false),
            'is_owner', COALESCE(({dataSql}->>'is_owner')::boolean, false),
            'is_admin', COALESCE(({dataSql}->>'is_admin')::boolean, false),
            'is_restricted', COALESCE(({dataSql}->>'is_restricted')::boolean, false),
            'is_ultra_restricted', COALESCE(({dataSql}->>'is_ultra_restricted')::boolean, false),
            'is_bot', COALESCE(({dataSql}->>'is_bot')::boolean, false),
            'deleted', COALESCE(({dataSql}->>'deleted')::boolean, false),
            'avatar_72_id', {dataSql}->'profile'->>'archived_avatar_72_id',
            'avatar_512_id', {dataSql}->'profile'->>'archived_avatar_512_id'
        )
        """;

    // Every snapshot of the users matching whereSql, with its fields as `fields`,
    // the version it belongs to as `profile_id`, and whether it starts that
    // version as `starts`. Also id, public_id, timestamp and data.
    public static string SnapshotsSql(string whereSql) =>
        $"""
        SELECT *, first_value(id) OVER (PARTITION BY public_id, version ORDER BY timestamp) AS profile_id
        FROM (
            SELECT *, count(*) FILTER (WHERE starts) OVER (PARTITION BY public_id ORDER BY timestamp) AS version
            FROM (
                SELECT *, fields IS DISTINCT FROM lag(fields) OVER (PARTITION BY public_id ORDER BY timestamp) AS starts
                FROM (
                    SELECT id, public_id, timestamp, data, {FieldsSql("data")} AS fields
                    FROM "user"
                    WHERE {whereSql}
                ) snapshot
            ) marked
        ) numbered
        """;
}
