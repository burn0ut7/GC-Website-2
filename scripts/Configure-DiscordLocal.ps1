$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'GC.Web.csproj'
$clientId = Read-Host 'Discord application (client) ID'
$guildId = Read-Host 'Global Conflicts Discord server ID'
$roleId = Read-Host 'Developer role ID in that Discord server'
foreach ($id in @($clientId, $guildId, $roleId)) {
    if ($id -notmatch '^[0-9]{1,20}$' -or $id -match '^0+$') { throw 'Use numeric application, server and role IDs, not user IDs or names.' }
}
$secret = Read-Host 'OAuth client secret (not a bot token)' -AsSecureString
$botToken = Read-Host 'Bot token for the community application already in that Discord server' -AsSecureString
$connection = Read-Host 'GC web runtime database connection string' -AsSecureString
$plainSecret = [System.Net.NetworkCredential]::new('', $secret).Password
$plainConnection = [System.Net.NetworkCredential]::new('', $connection).Password
$plainBotToken = [System.Net.NetworkCredential]::new('', $botToken).Password
if ([string]::IsNullOrWhiteSpace($plainSecret) -or [string]::IsNullOrWhiteSpace($plainConnection) -or [string]::IsNullOrWhiteSpace($plainBotToken)) { throw 'All secret fields are required.' }
$settings = @{
    'Discord:Enabled' = 'true'
    'Discord:ClientId' = $clientId
    'Discord:ClientSecret' = $plainSecret
    'ConnectionStrings:Gc' = $plainConnection
    'Auth:KeysPath' = (Join-Path $env:LOCALAPPDATA 'GC-Website-2/auth-keys')
    'Discord:GuildId' = $guildId
    'Discord:DeveloperRoleId' = $roleId
    'Discord:BotToken' = $plainBotToken
}
try {
    $settings | ConvertTo-Json -Compress | dotnet user-secrets set --project $project
    if ($LASTEXITCODE -ne 0) { throw 'Could not save local settings.' }
}
finally { $settings.Clear(); $plainSecret = $null; $plainConnection = $null; $plainBotToken = $null; $secret.Dispose(); $connection.Dispose(); $botToken.Dispose() }
Write-Output 'Saved development settings outside the repository. Register https://localhost:5081/auth/discord/callback in Discord.'
Write-Output 'Trust the development certificate, then launch on HTTPS in Development mode as described in docs/discord-identity.md.'
