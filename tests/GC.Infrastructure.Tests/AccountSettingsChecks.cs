using System.Net.Http.Json;
using System.Text.Json;

internal static class AccountSettingsChecks
{
    public static async Task Run(HttpClient client)
    {
        var defaults=await client.GetFromJsonAsync<JsonElement>("/api/account/settings");
        if(defaults.GetProperty("videoVolume").GetInt32()!=25)throw new Exception("Unexpected default preferences");
        var input=new {displayName="  Example player  ",timeZone="UTC",clockFormat="24h",videoVolume=37,reduceMotion=true,userId=Guid.NewGuid()};
        using(var denied=await client.PutAsJsonAsync("/api/account/settings",input))
            if((int)denied.StatusCode!=400)throw new Exception("Preferences changed without CSRF proof");
        using var csrf=await client.GetAsync("/api/auth/csrf");
        var token=(await csrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var originalCookie=string.Join("; ",client.DefaultRequestHeaders.GetValues("Cookie"));
        var cookie=originalCookie+"; "+string.Join("; ",csrf.Headers.GetValues("Set-Cookie").Select(value=>value.Split(';')[0]));
        client.DefaultRequestHeaders.Remove("Cookie");client.DefaultRequestHeaders.Add("Cookie",cookie);
        async Task<int> Put(object value)
        {
            using var request=new HttpRequestMessage(HttpMethod.Put,"/api/account/settings"){Content=JsonContent.Create(value)};
            request.Headers.Add("X-CSRF-TOKEN",token);
            using var response=await client.SendAsync(request);return (int)response.StatusCode;
        }
        try
        {
            if(await Put(input)!=200)throw new Exception("Valid preferences rejected");
            using var persisted=await client.GetAsync("/api/account/settings");
            if(persisted.Headers.CacheControl?.NoStore!=true)throw new Exception("Private preferences cached");
            var settings=await persisted.Content.ReadFromJsonAsync<JsonElement>();
            if(settings.GetProperty("displayName").GetString()!="Example player"||settings.GetProperty("videoVolume").GetInt32()!=37
                ||settings.GetProperty("timeZone").GetString()!="UTC"||settings.GetProperty("clockFormat").GetString()!="24h"
                ||!settings.GetProperty("reduceMotion").GetBoolean())throw new Exception("Preferences did not persist");
            foreach(var invalid in new object[]{new{displayName=new string('x',41)},new{displayName="bad\0name"},new{displayName=(string?)null},new{timeZone="Mars"},new{clockFormat="bad"},new{videoVolume=101},new{videoVolume=-1}})
                if(await Put(invalid)!=400)throw new Exception("Invalid preference accepted");
            if(await Put(new{extra=new string('x',9000)})!=413)throw new Exception("Unbounded preference body");
            var after=await client.GetFromJsonAsync<JsonElement>("/api/account/settings");
            if(after.GetProperty("videoVolume").GetInt32()!=37)throw new Exception("Invalid save altered preferences");
        }
        finally{client.DefaultRequestHeaders.Remove("Cookie");client.DefaultRequestHeaders.Add("Cookie",originalCookie);}
        Console.WriteLine("PASS: account preferences persist, validate, remain private and reject CSRF/oversized writes");
    }
}
