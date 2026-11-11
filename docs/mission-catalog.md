# Mission catalogue

Open `/missions`. Definitions come from the configured GitHub repositories; a played session remains a separate identity. The catalogue currently polls each source on worker startup and every ten minutes. This is bounded eventual consistency, not an instant webhook. `Launch.cmd` starts both the website and the worker when the private worker config exists.

## Configuration

Copy `appsettings.Worker.example.json` to `appsettings.Worker.Local.json` in the website root. Set its database connection using a worker account. The file is gitignored and must stay private. The worker's working directory must be this root (including when installed as a Windows service). Environment variables override the file. Apply migrations with the schema-owner account, then `scripts/Grant-Runtime-Access.sql` using the correct runtime role names.

`Catalog.Sources` contains repository and branch. Defaults are the GC missions and training repositories on `master`. No GitHub token or webhook setup is needed for these public repositories. Private repositories are not supported in this slice. Do not enable multiple copies with different source configurations; source advisory locks prevent simultaneous refreshes but do not arbitrate configuration differences.

Enable `Missions.Enabled` with the private `Ingest.ArchivePath` to project received statistics/history and backfill team details from existing validated archives. Delivery and telemetry projection retain their separate configuration switches; catalogue synchronization does not enable file delivery implicitly.

## Metadata and updates

The importer pins a complete GitHub tree to a commit, reads `Missions/**/*.conf` and the paired `.conf.meta`, and stores native scenario/world GUIDs. The actual path wins over stale `Name` paths inside meta files. A changed snapshot is published atomically. Deleted definitions become archived, preserving history. HTTP failures retain the last successful snapshot and expose an error in `/api/catalog/status`.

An invalid individual header/sidecar produces a visible source issue. Its previous valid definition is retained, if present; a new invalid header is omitted. Duplicate native scenario IDs or a truncated tree reject the snapshot. Importer version changes reparse cached definitions.

Metadata comes from the native Lobby2 `m_GC_Catalog` block, existing GC `m_GC_Metadata`, and optional authored sidecars named `Example.conf.metadata.json`. Explicit sidecar fields override header values; omitted fields preserve header values. Recognized title prefixes are only the legacy mission-type fallback:

```json
{"tags":["Infantry","Night","Combined arms"]}
```

The old website's manually maintained MongoDB tags are not in these repositories. They need a separate export/migration; this importer does not invent them. Native header player counts remain unknown when absent. Mission descriptions preserve Enfusion string continuations.

## Session links and access

New Lobby2 history includes `scenarioResource`, read from the native mission header. Exact scenario GUID links a session to a definition; older history may link through a unique world GUID. Ambiguous matches remain unlinked. Names and display names never establish identity. A mission world can have multiple scenario definitions.

Statistics retain the latest cumulative snapshot, including assignment intervals and roles. Session history contains commander intervals, factions, explicit outcome and winners; unknown outcomes remain unknown. A participating team not listed among explicit winners is labelled “Did not win.” Player counters shown in team lists are labelled session totals because a player may switch teams. Arma player identity links data within a session; Discord account association still needs a verified Arma account-linking flow.

The catalogue and basic play counts are public. Detailed session/player history and replay remain Developer-only, matching existing playback access. No authentication is bypassed by a replay link. The link selects its exact recording even when older than the first recording page.

## Server API

The server uses the existing HTTPS machine credential configured under `Ingest` (`Enabled`, `ApiKey`, `ServerId`, `ArchivePath`). Send `Authorization: Bearer <server-key>`; never place this key in a player client or public browser script.

- `GET /api/v1/catalog?search=&tag=&offset=0`: up to 50 definitions, `scenarioId`, metadata, counts and `nextOffset`.
- `GET /api/v1/catalog/{id}`: full definition.
- `GET /api/v1/catalog/status`: source commit/check/error/issue state.
- `GET /api/v1/catalog/{id}/sessions?offset=0`: only the authenticated server's sessions.
- Existing `/api/v1/missions/{sessionId}`, `/players`, `/events` retain session semantics and server scope.

Archived entries have `available:false` and must not be offered as launchable scenarios. Repository presence does not prove the addon/version is installed on a particular game server. The server should validate `scenarioId` against its installed resources before offering/launching a mission. This slice exposes the read contract; mission voting, scheduling and a Lobby2 mission-selection consumer remain separate work.

## Validation

`dotnet run --project tests/GC.Infrastructure.Tests -- --catalog` with `GC_TEST_ADMIN_CONNECTION` creates and removes an isolated test database. Checks cover parsing, caching, atomic failure, archived definitions, session matching, ambiguity, assignment projection/backfill and public/admin/machine access. `--explorer` covers existing four-stream intake and data viewing/removal regression. Local data is not used as a test fixture.


## Local test library

The local setup lists only Arland Lobby. Remote catalogue updates stay disabled. The worker reads the paths in `Catalog:LocalMission:Manifest`, and `Header` at startup and every ten minutes. Refresh mission metadata in Workbench after editing the mission. The list and detail page use the same imported record.

To reproduce after applying migrations, run the website executable with the schema-owner connection and arguments `--import-local-mission data/mission-catalog/arland-lobby.json --replace-mission-list`. Run from the checkout. This hides prior entries and upserts the test definition without changing its identity on repeat imports. Omit the replacement flag to add a definition without hiding others.

The manifest is authored test metadata. Tags Testing/Lobby2 describe this test entry; author and capacity are unknown. World GUID comes from Lobby2 worlds/examples/arland/Arland_Lobby.ent.meta. The local world-only definition keeps scenarioId null and available false: the server must not offer it as a launchable scenario. Its authored metadata now comes from Lobby2 `Configs/Missions/ArlandLobby.conf`, an unlisted authoring example; this local import does not invent a launchable scenario GUID. Public UI shows Test mission rather than Archived. Once a real mission header is authored, import its actual scenario identity.

Legacy Workbench history using the exact authored $Lobby2 world alias can match this definition. Alias matches must be unique across all stored definitions; names and terrain names are never used for matching. Hidden definitions continue participating in ambiguity checks. Detailed statistics/history remain Developer-only; summaries count recorded sessions, distinct player identities, recordings and cumulative player counters without synthesizing missing values.


## Categorized authored metadata

Local manifests and repository .conf.metadata.json sidecars support:

```json
{
  "missionType": "COOP",
  "objectives": ["Convoy", "Search & Rescue"],
  "mods": ["Lobby2"],
  "assetTypes": ["Infantry", "Helicopters"],
  "tags": ["Night"]
}
```

Mission types: COOP (CO-OP), TVT, COTVT, TRNG (Training), LOL, TEST (Testing), BROKEN. Objective suggestions include Defense, Attack & Defend, Assault, Convoy, HVT, Sweep, Hostage, Delay, Search & Rescue. Multiple objectives are allowed; custom authored objective labels are supported. Categories remain distinct in metadata while all values join the tag filter. Native mission header description and player capacity retain their existing source; local manifests use description and maxPlayers. Missing data is shown as Not specified, never inferred from a prior session population.

Arland Lobby is labelled TEST. Its objectives and assets are unspecified pending authored or verified exported metadata; Lobby2 is known from its owning addon. Mods here are authored metadata, not an exhaustive dependency scan. The experimental Lobby2 placed-content scanner is not automatically run by this website importer.


## Native Lobby2 / GC contract (version 1)

Lobby2 `GC_MissionHeader.m_GC_Catalog` exposes mission type, objectives, minimum/recommended players, era, player equipment, mod names, and Factions (`m_aFactions`). Header `m_iPlayerCount` supplies maximum capacity when explicitly serialized. Manual Tags, Map Name, Required Addon IDs and mission grouping fields are hidden legacy serialization fields, not authoring inputs. Existing saved data remains readable for compatibility. New faction entries override old `m_aForces` when present. Description, detailed rules and preview-resource references use standard header fields. Preview resources are retained as resource references; this slice does not convert game images for the browser.

The importer also reads existing GC `m_GC_Metadata`: forces, era, special player-used assets and mission groups. Legacy opposing forces are **not** automatically labelled playable. New force entries carry optional playable/AI-only status and intended player slots; these describe the mission and do not modify its actual slot configuration.

The normalized JSON contract below also retains legacy fields for older imports. The website no longer displays manual tags, declared addon IDs or mission groups. Map names are resolved from the mission world against the same registered map identities as playback; unknown or conflicting matches remain unknown. Only map names and world suffixes are exposed publicly, not protected replay assets. A registry lookup failure does not block the mission list. Faction display merges declared details with configured/placed factions from saved metadata, without inferring player slots. Mod names are not treated as addon IDs; complete transitive dependencies remain unimplemented.

The normalized JSON fields accepted by repository sidecars and local manifests are:

```json
{
  "schemaVersion": 1,
  "missionType": "COOP",
  "map": "Arland",
  "minPlayers": 12,
  "recommendedPlayers": 24,
  "era": "1980s",
  "objectives": ["Convoy", "Search & Rescue"],
  "assetTypes": ["Trucks"],
  "mods": ["Lobby2"],
  "requiredAddonIds": ["6A115D5DFF7ED870"],
  "forces": [{"faction":"US","forceType":"MOTORIZED","playable":true,"slots":32}],
  "missionGroup": {"id":"Convoy variants","similarity":0.75},
  "tags": ["Night"]
}
```

Counts must be positive when supplied, ordered minimum <= recommended <= maximum when each is known, and at most 10,000. Native optional counts use zero for unspecified; JSON uses omission/null. Force `playable` omission/null means unknown, not AI. Similarity ranges from zero to one. Objective labels are suggestions, not a closed enum. Unknown versions, invalid counts/types, duplicate native fields and unresolved config inheritance are rejected with the existing source-issue/last-good-version behavior.

Repository publication: save and commit the mission `.conf` plus its `.conf.meta`; the existing worker fetches it on startup/every ten minutes when `Catalog.Enabled` is true. No manual website import button is needed. Standard and subclassed serialized mission headers are supported. Inherited configs must be published as resolved headers; the importer is not an Enfusion resource inheritance engine.

Generated world content is optional and embedded in the native mission header as `m_GC_GeneratedMetadata GC_GeneratedMissionMetadata`. Its fields include `m_iSchemaVersion`, `m_sWorld`, `m_sCaptureScope`, limits, factions, slot rows, detected assets, addon provenance and starting-time evidence. The header hash covers generated changes. The importer verifies world identity/version and retains a bounded summary under `metadata.generated`; prefab detail rows are excluded from public catalogue responses. Native attributed defaults (including zero slot indexes and false booleans) are respected. Header reads are bounded to 8 MiB. Detected assets never become player equipment, player capacity, objective tags or complete required dependencies. The collector covers one mission subscene and excludes runtime spawners and other subscenes. Save/refresh before publishing; a repository commit does not prove the scan was refreshed. Old `.ent.metadata.json` files are no longer fetched. Refresh existing missions in Workbench, verify the embedded data, then remove those exports.

TilW conditions, hidden objectives and game-ending instructions are not copied into public metadata or guessed into semantic objective tags. Effective time/weather extraction, transitive addon dependency resolution and historical definition-version snapshots remain separate work. Existing session history/outcomes/statistics are preserved independently.

### Local Arland proof

Run from the website checkout using the schema-owner connection:

```powershell
dotnet bin/Release/net10.0/GC.Web.dll --import-local-mission data/mission-catalog/arland-lobby.json --mission-header "C:\Users\Gray\Documents\My Games\ArmaReforgerWorkbench\addons\Lobby2\Configs\Missions\Gray\ArlandLobby.conf"
```

Generated content is read directly from the supplied native header. `--mission-content` is retired and rejected. These are explicit local CLI file inputs, never remote-controlled server paths. The manifest retains stable local identity and historical world aliases; the header supplies mission metadata. This updates Arland without listing old repository missions or deleting history. The local test setup intentionally keeps repository sync disabled.

Checks: `--mission-metadata` runs pure contract tests; `--catalog` runs the isolated database/API/import suite; `--mission-source <Missions-directory>` validates real local headers without writing them or changing the catalogue.

### World-derived slots (2026-11-03)
GC catalogue minimum/recommended counts and manual forces are retired. Player Count on the mission header supplies capacity. The optional world sidecar now contains playerSlots (group, index, faction, prefab, name, loadoutFaction); website intake validates these and mission details lists them. Factions display observed keys, not every configured/unused faction. Loadout prefab references are authored choices, not resolved runtime variants or full inventory snapshots. Regenerate with Save Mission and Metadata; Ctrl+S alone does not refresh the sidecar.

