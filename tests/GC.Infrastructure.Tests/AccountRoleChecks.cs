using System.Net;
using System.Text;
using GC.Web;
using Microsoft.Extensions.Configuration;

internal static class AccountRoleChecks
{
    private sealed class Provider : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public string Mode = "ok";
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (request.Headers.Authorization?.Scheme != "Bot") throw new Exception("Missing server authentication");
            if (Mode == "missing") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (Mode == "unavailable") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var member = request.RequestUri!.AbsolutePath.Contains("/members/");
            var body = member ? "{\"user\":{\"id\":\"123\"},\"roles\":[\"dev\",\"member\"]}" : "[{\"id\":\"dev\",\"name\":\"Developer\",\"position\":2,\"color\":12345},{\"id\":\"member\",\"name\":\"Member\",\"position\":1,\"color\":0},{\"id\":\"other\",\"name\":\"Other\",\"position\":3,\"color\":0}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
    public static async Task Run()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Discord:GuildId"]="guild", ["Discord:BotToken"]="test", ["Discord:DeveloperRoleId"]="dev" }).Build();
        var provider = new Provider();
        var service = new AccountRoles(config, provider);
        var result = await service.Read("123", default);
        if (result.Status != "ok" || result.Roles.Length != 2 || result.Roles[0].Name != "Developer" || result.Roles[0].Access.Length != 3 || result.Roles[1].Access.Length != 0) throw new Exception("Role assignment/access display mismatch");
        await service.Read("123", default);
        if (provider.Calls != 2) throw new Exception("Role snapshot not cached");
        if ((await service.Read("456", default)).Status != "unavailable") throw new Exception("Mismatched member accepted");
        provider.Mode="missing";
        if ((await new AccountRoles(config,provider).Read("123",default)).Status != "not_member") throw new Exception("Missing member mishandled");
        provider.Mode="unavailable";
        if ((await new AccountRoles(config,provider).Read("123",default)).Status != "unavailable") throw new Exception("Failure reported as empty roles");
        Console.WriteLine("PASS: assigned roles, access descriptions, cache, identity verification and failure states");
    }
}
