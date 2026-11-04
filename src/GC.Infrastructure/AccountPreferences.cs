using Npgsql;

namespace GC.Infrastructure;

public sealed record AccountPreferences(string DisplayName = "", string TimeZone = "local",
    string ClockFormat = "auto", int VideoVolume = 25, bool ReduceMotion = false)
{
    public AccountPreferences Validated()
    {
        if (DisplayName is null || DisplayName.Length > 40 || DisplayName.Any(char.IsControl)
            || TimeZone is not ("local" or "UTC" or "Europe/Berlin")
            || ClockFormat is not ("auto" or "12h" or "24h") || VideoVolume is < 0 or > 100)
            throw new ArgumentException("Invalid account preferences.");
        return this with { DisplayName = DisplayName.Trim() };
    }

    public static async Task<AccountPreferences> Read(NpgsqlDataSource database, Guid user, CancellationToken token)
    {
        await using var command = database.CreateCommand("SELECT display_name,time_zone,clock_format,video_volume,reduce_motion FROM gc.account_preferences WHERE user_id=$1");
        command.Parameters.AddWithValue(user);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3),reader.GetBoolean(4))
            : new();
    }

    public static async Task<AccountPreferences> Save(NpgsqlDataSource database, Guid user, AccountPreferences value, CancellationToken token)
    {
        value = value.Validated();
        await using var command = database.CreateCommand("""
            INSERT INTO gc.account_preferences(user_id,display_name,time_zone,clock_format,video_volume,reduce_motion)
            VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(user_id) DO UPDATE SET
                display_name=excluded.display_name,time_zone=excluded.time_zone,clock_format=excluded.clock_format,
                video_volume=excluded.video_volume,reduce_motion=excluded.reduce_motion
            """);
        command.Parameters.AddWithValue(user); command.Parameters.AddWithValue(value.DisplayName);
        command.Parameters.AddWithValue(value.TimeZone); command.Parameters.AddWithValue(value.ClockFormat);
        command.Parameters.AddWithValue(value.VideoVolume); command.Parameters.AddWithValue(value.ReduceMotion);
        await command.ExecuteNonQueryAsync(token);
        return value;
    }
}
