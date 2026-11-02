$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'GC.Web.csproj'
$clientId = Read-Host 'Discord application (client) ID'
$adminId = Read-Host 'Discord user ID allowed to inspect data'
if ($clientId -notmatch '^\d{1,32}$' -or $adminId -notmatch '^\d{1,32}$') { throw 'Use numeric Discord IDs, not names.' }
$secret = Read-Host 'OAuth client secret (not a bot token)' -AsSecureString
$connection = Read-Host 'GC web runtime database connection string' -AsSecureString
$plainSecret = [System.Net.NetworkCredential]::new('', $secret).Password
$plainConnection = [System.Net.NetworkCredential]::new('', $connection).Password
if ([string]::IsNullOrWhiteSpace($plainSecret) -or [string]::IsNullOrWhiteSpace($plainConnection)) { throw 'Both secret fields are required.' }
$settings = @{
    'Discord:Enabled' = 'true'
    'Discord:ClientId' = $clientId
    'Discord:ClientSecret' = $plainSecret
    'ConnectionStrings:Gc' = $plainConnection
    'Auth:KeysPath' = (Join-Path $env:LOCALAPPDATA 'GC-Website-2/auth-keys')
    'Auth:AdminDiscordIds:0' = $adminId
}
try {
    $settings | ConvertTo-Json -Compress | dotnet user-secrets set --project $project
    if ($LASTEXITCODE -ne 0) { throw 'Could not save local settings.' }
}
finally { $settings.Clear(); $plainSecret = $null; $plainConnection = $null; $secret.Dispose(); $connection.Dispose() }
Write-Output 'Saved development settings outside the repository. Register https://localhost:5081/auth/discord/callback in Discord.'
Write-Output 'Trust the development certificate, then launch on HTTPS in Development mode as described in docs/discord-identity.md.'
