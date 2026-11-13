$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $root
try {
    $localConfig = Join-Path $root 'appsettings.Local.json'
    if (Test-Path -LiteralPath $localConfig) {
        $pgRoot = (Get-Content -LiteralPath $localConfig -Raw | ConvertFrom-Json).LocalPostgresRoot
        if ($pgRoot) {
            $pgCtl = Join-Path $pgRoot 'postgres\bin\pg_ctl.exe'
            $pgData = Join-Path $pgRoot 'data'
            & $pgCtl -D $pgData status *> $null
            if ($LASTEXITCODE -eq 3) {
                Write-Host 'Starting the website database...'
                & $pgCtl -D $pgData -l (Join-Path $pgRoot 'logs\postgres.log') -w start
            }
            if ($LASTEXITCODE -ne 0) { throw 'The website database could not start.' }
        }
    }
    # Only this checkout's website processes; never stop all dotnet processes.
    $processes = @(Get-CimInstance Win32_Process | Where-Object {
        $commandLine = ([string]$_.CommandLine).Replace('/', '\')
        ($_.Name -in @('GC.Web.exe','GC.Worker.exe') -and ([string]$_.ExecutablePath).StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) -or
        ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match 'GC\.(Web|Worker)(\.dll|\.csproj)' -and
            $commandLine.IndexOf($root + '\',[StringComparison]::OrdinalIgnoreCase) -ge 0)
    })
    foreach ($process in $processes) {
        Write-Host "Stopping previous website instance ($($process.ProcessId))..."
        Stop-Process -Id $process.ProcessId -ErrorAction Stop
        Wait-Process -Id $process.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
    }
    Write-Host 'Building website...'
    & dotnet build (Join-Path $root 'GC.Web.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. The website was not started.' }
    $worker = $null
    if (Test-Path -LiteralPath (Join-Path $root 'appsettings.Worker.Local.json')) {
        & dotnet build (Join-Path $root 'src\GC.Worker\GC.Worker.csproj') -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Worker build failed. The website was not started.' }
        $logs = Join-Path $root '.local'
        New-Item -ItemType Directory -Force -Path $logs | Out-Null
        $workerDll = Join-Path $root 'src\GC.Worker\bin\Release\net10.0\GC.Worker.dll'
        $worker = Start-Process dotnet -ArgumentList @('"' + $workerDll + '"') -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs 'worker.log') -RedirectStandardError (Join-Path $logs 'worker-error.log')
    }
    Write-Host 'Starting website. Open the listening address printed below. Press Ctrl+C to stop.'
    try { & dotnet (Join-Path $root 'bin\Release\net10.0\GC.Web.dll') }
    finally { if ($worker -and -not $worker.HasExited) { Stop-Process -Id $worker.Id } }
    exit $LASTEXITCODE
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
