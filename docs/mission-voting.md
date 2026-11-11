# Mission voting catalog

## Production configuration

Enable the existing ingest service with `Ingest:Enabled=true`. Set `Ingest:ApiKey` to a random key of at least 32 characters and `Ingest:ServerId` to the existing server UUID. Set `Ingest:ArchivePath` to a private absolute path outside the webroot. Keep the existing database configuration.

Supply the API key through deployment configuration, such as `Ingest__ApiKey`. Do not commit the key or expose it to browsers.

The dedicated game server reads its private `$profile:Lobby2Voting.json` file:

```json
{
  "url": "https://catalog.example/",
  "apiKey": "<same private machine key>"
}
```

These values are illustrative placeholders. Use the website base URL; the game adds a trailing slash when needed. Restrict the profile file with filesystem permissions so only the service account can read it. Do not package it in the addon.

## Catalog and session data

The game server uses bearer authentication for `GET api/v1/catalog/voting?offset=0`. Responses contain pages of 25; continue using the endpoint's offset pagination. The endpoint is server-only.

Voting choices come from listed available catalog headers (`.conf` files) with valid GUIDs. The game checks that each header is installed. A metadata `previewResource` must be an installed game resource, not a web URL. Existing `catalog_sessions` records in `running` or `completed` state provide session counts and the last started time in UTC.

The admin load-browser is out of scope for mission voting.
