using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ApiChecks
{
    public static async Task Run(string web, string connection)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var archive = Path.Combine(Path.GetTempPath(), "gc-test-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(web); start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ConnectionStrings__Gc"] = connection;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Discord__Enabled"] = "false";
        start.Environment["Ingest__Enabled"] = "true";
        start.Environment["Ingest__ApiKey"] = key;
        start.Environment["Ingest__ArchivePath"] = archive;
        start.Environment["Ingest__ServerId"] = Guid.NewGuid().ToString();
        var migrationStart = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        migrationStart.ArgumentList.Add(web); migrationStart.ArgumentList.Add("--migrate");
        migrationStart.Environment["ConnectionStrings__Gc"] = connection;
        using (var migration = Process.Start(migrationStart)!)
        {
            var migrationOutput = migration.StandardOutput.ReadToEndAsync();
            var migrationErrors = migration.StandardError.ReadToEndAsync();
            using var migrationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await migration.WaitForExitAsync(migrationTimeout.Token); }
            finally { if (!migration.HasExited) migration.Kill(entireProcessTree: true); }
            if (migration.ExitCode != 0) throw new Exception("Migration CLI failed: " + await migrationErrors);
            if (!(await migrationOutput).Contains("Database migrations applied.")) throw new Exception("Migration CLI did not complete");
        }
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        Task<string>? output = null;
        try
        {
            string? address = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                const string prefix = "Now listening on: ";
                var offset = line.IndexOf(prefix, StringComparison.Ordinal);
                if (offset >= 0) { address = line[(offset + prefix.Length)..].Trim(); break; }
            }
            output = process.StandardOutput.ReadToEndAsync();
            if (address is null) throw new Exception("Test web app failed to start: " + await errors);
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
            await Expect(client.GetAsync("/api/ready"), HttpStatusCode.OK);
            await Expect(client.GetAsync("/api/me"), HttpStatusCode.Unauthorized);
            await Expect(client.GetAsync("/auth/discord"), HttpStatusCode.ServiceUnavailable);
            var route = "/api/v1/ingest/telemetry/" + Guid.NewGuid() + "/0";
            await Expect(client.PostAsync(route, new ByteArrayContent([1, 2, 3])), HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            await Expect(client.PostAsync(route, new ByteArrayContent([1, 2, 3])), HttpStatusCode.Accepted);
            await Expect(client.PostAsync(route, new ByteArrayContent([1, 2, 3])), HttpStatusCode.Accepted);
            if (Directory.GetFiles(archive, "*.batch").Length != 1) throw new Exception("Duplicate archive created");
            await Expect(client.PostAsync(route, new ByteArrayContent([4, 5, 6])), HttpStatusCode.Conflict);
            await Expect(client.PostAsync(route, new ByteArrayContent(new byte[1048577])), HttpStatusCode.RequestEntityTooLarge);
            await Expect(client.PostAsync(route, new ByteArrayContent([])), HttpStatusCode.BadRequest);
            await Expect(client.GetAsync("/appsettings.json"), HttpStatusCode.NotFound);
            Console.WriteLine("PASS: API readiness, anonymous auth boundaries, intake authentication, duplicate/conflict, size limits and private files");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var logs = (output is null ? "" : await output) + await errors;
            if (Directory.Exists(archive) && Path.GetFileName(archive).StartsWith("gc-test-", StringComparison.Ordinal)
                && Path.GetDirectoryName(archive) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar))
                Directory.Delete(archive, recursive: true);
            if (logs.Contains(key, StringComparison.Ordinal)) throw new Exception("API credential leaked into logs");
        }
    }

    private static async Task Expect(Task<HttpResponseMessage> request, HttpStatusCode expected)
    {
        using var response = await request;
        if (response.StatusCode != expected)
            throw new Exception($"Expected {(int)expected}, received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
