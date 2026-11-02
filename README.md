# Global Conflicts website

Small ASP.NET Core (.NET 10) host for the community landing page.

## Run locally

Install the .NET 10 SDK. From this directory:

```powershell
dotnet run --project GC.Web.csproj
```

Open http://127.0.0.1:5080/#home. Alternatively, double-click `Launch.cmd`
and open the address it prints. Keep the console open; Ctrl+C stops the server.
The default listener is loopback-only. Python is no longer required.

## Current scope

- Existing landing page, images, fonts, Discord links and introduction video.
- `GET /api/health` returns HTTP 200 with `{"status":"ok"}` (process liveness only).
- Console request logs: method, path, status and duration; no query strings,
  request bodies, cookies or authorization headers.
- Unknown API endpoints and missing files return 404, not landing-page HTML.

There is no database, login, worker, Discord bot or real playback service yet.
The next slice is PostgreSQL connectivity and one versioned migration.

## Editing and validation

`dist/` is the single source for public frontend assets. The build copies those
files to application output; rebuild/restart after edits. Only `dist/` is
served publicly. Old hash links return to the landing page. Video requires
internet access and starts at 25% volume.

```powershell
dotnet build --configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Smoke-Test.ps1
```

The smoke check publishes into ignored `artifacts/smoke`, starts on an available
loopback port, checks health/static file integrity/private-file isolation and
restart behavior, then stops its child process. Logs remain in that folder.

## Publish a portable application folder

```powershell
dotnet publish --configuration Release --output artifacts/publish
dotnet artifacts/publish/GC.Web.dll
```

The published app requires the .NET 10 ASP.NET Core runtime and includes the
landing assets. No production service or IIS configuration is installed by
these commands. HTTPS, production host names and reverse-proxy configuration
belong to a later deployment slice. Keep loopback defaults for local work.

The existing `.openai/hosting.json` describes the earlier static Sites preview;
it cannot deploy this .NET backend. GitHub is now the backend source repository.
Font license information remains in `dist/fonts/`.
