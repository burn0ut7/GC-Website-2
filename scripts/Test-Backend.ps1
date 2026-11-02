$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$env:GC_TEST_ADMIN_CONNECTION) { throw 'Set GC_TEST_ADMIN_CONNECTION for a local PostgreSQL test administrator.' }
$web = Join-Path $root 'artifacts/backend-test-web'
$worker = Join-Path $root 'artifacts/backend-test-worker'
dotnet publish (Join-Path $root 'GC.Web.csproj') -c Release -o $web
if ($LASTEXITCODE -ne 0) { throw 'Web publish failed.' }
dotnet publish (Join-Path $root 'src/GC.Worker/GC.Worker.csproj') -c Release -o $worker
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
$previousWeb = $env:GC_TEST_WEB_DLL
$previousWorker = $env:GC_TEST_WORKER_DLL
try {
    $env:GC_TEST_WEB_DLL = Join-Path $web 'GC.Web.dll'
    $env:GC_TEST_WORKER_DLL = Join-Path $worker 'GC.Worker.dll'
    dotnet run --project (Join-Path $root 'tests/GC.Infrastructure.Tests') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Backend integration checks failed.' }
}
finally {
    $env:GC_TEST_WEB_DLL = $previousWeb
    $env:GC_TEST_WORKER_DLL = $previousWorker
}
