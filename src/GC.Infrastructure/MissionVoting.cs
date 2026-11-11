using System.Text.Json;
using System.Text;
using System.Text.Encodings.Web;
using Npgsql;

namespace GC.Infrastructure;

/// <summary>Bounded server-only projection of the existing catalogue; gameplay owns ballots.</summary>
public static class MissionVoting
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<object> Candidates(NpgsqlDataSource db, int offset, CancellationToken ct)
    {
        if (offset < 0 || offset > 100000) throw new ArgumentException("Invalid page");
        await using var query = db.CreateCommand("""
            SELECT m.id, m.scenario_guid, m.scenario_path, m.name, m.description, m.tags,
                coalesce(m.metadata->'data'->>'previewResource',''),
                (SELECT count(*) FROM gc.catalog_sessions s WHERE s.catalog_id=m.id
                    AND s.metadata->>'status' IN ('running','completed')),
                (SELECT max(started_at) FROM gc.catalog_sessions s WHERE s.catalog_id=m.id
                    AND s.metadata->>'status' IN ('running','completed')),
                m.max_players, m.metadata->'data'->>'minPlayers'
            FROM gc.catalog_missions m
            WHERE m.listed AND m.available AND m.scenario_guid ~ '^[0-9A-F]{16}$'
                AND m.scenario_path LIKE '%.conf'
                AND coalesce(m.metadata->'data'->>'missionType','') <> 'BROKEN'
            ORDER BY m.id LIMIT 25 OFFSET $1
            """);
        query.Parameters.AddWithValue(offset);
        var rows = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            // Individual JSON records are also the bounded game RPC unit.
            var description = Short(reader.GetString(4), 600);
            var tags = Short(string.Join(" · ", reader.GetFieldValue<string[]>(5)), 180);
            string row;
            do
            {
                row = JsonSerializer.Serialize(new {
                id = reader.GetGuid(0).ToString(),
                scenario = "{" + reader.GetString(1) + "}" + reader.GetString(2),
                name = Short(reader.GetString(3), 100),
                maxPlayers = reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9),
                minPlayers = !reader.IsDBNull(10) && int.TryParse(reader.GetString(10), out var minimum) ? (int?)minimum : null,
                description,
                tags,
                thumbnail = Short(reader.GetString(6), 512),
                plays = reader.GetInt64(7).ToString(System.Globalization.CultureInfo.InvariantCulture),
                lastPlayed = reader.IsDBNull(8) ? "Never played" :
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(8)).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'",
                        System.Globalization.CultureInfo.InvariantCulture)
                }, Json);
                if (Encoding.UTF8.GetByteCount(row) <= 3600) break;
                description = Short(description, Math.Max(1, description.Length / 2));
                tags = Short(tags, Math.Max(1, tags.Length / 2));
            } while (description.Length > 1 || tags.Length > 1);
            if (Encoding.UTF8.GetByteCount(row) > 3600) throw new InvalidDataException("Voting record exceeds transport limit");
            rows.Add(row);
        }
        return new { version = 1, rows, nextOffset = rows.Count == 25 ? offset + 25 : -1 };
    }

    private static string Short(string value, int limit)
    {
        if (value.Length <= limit) return value;
        var end = limit - 1;
        if (end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end] + "…";
    }
}
