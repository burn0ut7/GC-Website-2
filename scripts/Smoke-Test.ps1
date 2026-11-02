$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts/smoke'
dotnet publish (Join-Path $root 'GC.Web.csproj') --configuration Release --output $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(5)
$client.DefaultRequestHeaders.Add('Authorization', 'Bearer smoke-secret-header')
try {
    foreach ($run in 1..2) {
        $stdout = Join-Path $output "run-$run.log"
        $stderr = Join-Path $output "run-$run.err.log"
        $process = Start-Process dotnet -ArgumentList @(
            ('"' + (Join-Path $output 'GC.Web.dll') + '"'),
            '--urls', 'http://127.0.0.1:0'
        ) -WorkingDirectory $output -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        try {
            $base = $null
            for ($attempt = 0; $attempt -lt 100; $attempt++) {
                if ($process.HasExited) { throw "Server exited: $(Get-Content $stderr -Raw)" }
                $log = Get-Content $stdout -Raw -ErrorAction SilentlyContinue
                if ($log -match 'Now listening on: (http://127\.0\.0\.1:\d+)') {
                    $base = $Matches[1]; break
                }
                Start-Sleep -Milliseconds 100
            }
            if (!$base) { throw 'Server did not become ready.' }
            $health = $client.GetAsync("$base/api/health?probe=smoke-secret-query").Result
            if ([int]$health.StatusCode -ne 200 -or
                ($health.Content.ReadAsStringAsync().Result | ConvertFrom-Json).status -ne 'ok') {
                throw 'Health check failed.'
            }
            foreach ($relative in @('index.html','app.js','schedule.js','styles.css','squad.jpg','intro-thumbnail.jpg','discord.svg','fonts/Lordcorps.ttf')) {
                $urlPath = if ($relative -eq 'index.html') { '' } else { $relative }
                $response = $client.GetAsync("$base/$urlPath").Result
                if ([int]$response.StatusCode -ne 200) { throw "Asset failed: $relative" }
                $download = Join-Path $output 'asset-check.tmp'
                [IO.File]::WriteAllBytes($download, $response.Content.ReadAsByteArrayAsync().Result)
                if ((Get-FileHash $download).Hash -ne (Get-FileHash (Join-Path $root "dist/$relative")).Hash) {
                    throw "Asset bytes changed: $relative"
                }
                $response.Dispose()
            }
            foreach ($path in @('/api/missing','/missing.png','/appsettings.json','/Program.cs','/.git/config','/.openai/hosting.json')) {
                $response = $client.GetAsync("$base$path").Result
                if ([int]$response.StatusCode -ne 404) { throw "Expected 404: $path" }
                $response.Dispose()
            }
            $health.Dispose()
            foreach ($check in @(@('/api/ready',503), @('/auth/discord',503), @('/api/me',401))) {
                $response = $client.GetAsync("$base$($check[0])").Result
                if ([int]$response.StatusCode -ne $check[1]) { throw "Unexpected status: $($check[0])" }
                $response.Dispose()
            }
            Write-Output "Run ${run}: health, asset integrity and private-file isolation passed."
        }
        finally {
            if (!$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
            $process.Dispose()
        }
        $logs = (Get-Content $stdout -Raw) + (Get-Content $stderr -Raw)
        if ($logs -match 'smoke-secret') { throw 'Request credentials or query string leaked into logs.' }
        if ($logs -notmatch 'StatusCode: 200' -or $logs -notmatch 'GET') { throw 'Request logs missing.' }
    }
    Write-Output 'PASS: published application starts twice; request logs exclude query strings and authorization.'
}
finally { $client.Dispose() }
