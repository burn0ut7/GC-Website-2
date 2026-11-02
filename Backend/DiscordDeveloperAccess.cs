using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GC.Web;

// The configured guild's Developer role is the sole authority. Never cache positive grants
// in cookies or retain stale permission when Discord cannot verify membership.
public sealed class DiscordDeveloperAccess(IConfiguration configuration, IHttpClientFactory clients)
{
    private long retryAfterTicks;

    public async Task<bool> HasRole(string subject, CancellationToken token)
    {
        var guild = configuration["Discord:GuildId"];
        var role = configuration["Discord:DeveloperRoleId"];
        var credential = configuration["Discord:BotToken"];
        if (!Snowflake(guild) || !Snowflake(role) || !Snowflake(subject) || string.IsNullOrWhiteSpace(credential)
            || DateTime.UtcNow.Ticks < Interlocked.Read(ref retryAfterTicks)) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://discord.com/api/v10/guilds/{guild}/members/{subject}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", credential);
            using var client = clients.CreateClient("discord-roles");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(30);
                Interlocked.Exchange(ref retryAfterTicks, DateTime.UtcNow.AddSeconds(Math.Clamp(delay.TotalSeconds, 1, 3600)).Ticks);
                return false;
            }
            if (!response.IsSuccessStatusCode) return false;
            // Bound both body size and the full read duration, not just response headers.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int count;
            while ((count = await body.ReadAsync(block, deadline.Token)) > 0)
            {
                if (buffer.Length + count > 128 * 1024) return false;
                buffer.Write(block, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            // Verify the response belongs to the requested, server-verified Discord identity.
            return root.GetProperty("user").GetProperty("id").GetString() == subject
                && root.GetProperty("roles").EnumerateArray().Any(value => value.GetString() == role);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException
            or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IOException)
        {
            return false;
        }
    }

    private static bool Snowflake(string? value) => value is { Length: > 0 and <= 20 }
        && value.All(c => c is >= '0' and <= '9') && value.Any(c => c != '0');
}
