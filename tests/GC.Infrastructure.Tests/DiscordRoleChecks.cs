using GC.Web;
using Microsoft.Extensions.Configuration;
using System.Net;

internal static class DiscordRoleChecks
{
    public static async Task Run()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Discord:GuildId"]="123", ["Discord:DeveloperRoleId"]="456", ["Discord:BotToken"]="test-only"
        }).Build();
        var clock = new Clock();
        var handler = new Responses();
        var access = new DiscordDeveloperAccess(config, new Factory(handler), clock);
        var checks = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => access.HasRole("789", default)));
        if (checks.Any(x => !x) || handler.Calls != 1) throw new Exception("Concurrent role checks were not coalesced.");
        clock.Now += TimeSpan.FromSeconds(31);
        handler.Status = HttpStatusCode.TooManyRequests;
        if (await access.HasRole("789", default)) throw new Exception("Expired grant survived rate limit.");
        if (await access.HasRole("789", default) || handler.Calls != 2) throw new Exception("Rate limit was not respected.");
        clock.Now += TimeSpan.FromSeconds(31);
        handler.Status = HttpStatusCode.OK;
        handler.Roles = "[]";
        if (await access.HasRole("789", default)) throw new Exception("Removed role retained access.");
        handler.Roles = "[\"456\"]";
        config["Discord:DeveloperRoleId"] = "999";
        if (await access.HasRole("789", default)) throw new Exception("Configuration change reused old grant.");
        config["Discord:DeveloperRoleId"] = "456";
        clock.Now += TimeSpan.FromSeconds(31);
        using var cancelled = new CancellationTokenSource();
        handler.Cancel = cancelled;
        try { await access.HasRole("789", cancelled.Token); throw new Exception("Cancellation was swallowed."); }
        catch (OperationCanceledException) { }
        handler.Cancel = null;
        if (!await access.HasRole("789", default)) throw new Exception("Cancelled request poisoned the next role check.");
        Console.WriteLine("PASS: role check coalescing, expiry, rate-limit denial, revocation and configuration isolation");
    }
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Responses : HttpMessageHandler
    {
        public int Calls;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Roles = "[\"456\"]";
        public CancellationTokenSource? Cancel;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Cancel is not null) { Cancel.Cancel(); throw new OperationCanceledException(token); }
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("{\"user\":{\"id\":\"789\"},\"roles\":" + Roles + "}") });
        }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }
}
