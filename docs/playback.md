# Local playback

## Progressive loading

The first complete 10-second window enables Play. Remaining 30-second windows
load in the background, using pages of up to 500 records. The timeline ends at
the loaded boundary; playback waits there until more data arrives. Loaded state
is retained from the beginning so actor baselines are not invented. Filters,
colors, selection and playhead survive window updates. This is progressive
loading within the existing memory limit, not unlimited streaming or random
seeking into unloaded data.

Each request has a 15-second deadline. Failures show an error and Reload action;
partially loaded data remains usable. Map imagery and supplemental player
statistics do not block Play. Final completeness is checked against the summary.
Discord role checks use the bounded server cache described in discord-identity.md.
Regression checks cover cancellation, pagination failures and role-cache expiry.
A delayed browser test enabled Play at 7 / 1,241 records and then completed all
records while playing.

## Current producer update — engine contacts

Lobby2 now records native bullet contacts for ammunition inheriting the stock
bullet base. The latest imported recording is
`01a19dec-5106-8e74-8400-00000064f19e`: 1,241 records, including 53 player shots
plus one explicit native-flight probe, 110 linked contacts, 42 damage events,
3 AI kills and vehicle data. All player shots include separately observed
server muzzle positions. The renderer retains the projectile origin as its
shot marker; a muzzle sample is not silently substituted for it.

Playback associates contacts even if their callback precedes the shot callback.
Successive contacts connect the previous observed point to the next, rather
than repeatedly drawing from the original shot through a ricochet. These
segments do not establish the trajectory between observations or a final
resting point. Ammo overriding the base effects and non-bullet explosives need
separate coverage. Old recordings cannot gain missing contact evidence.

Production intake and read permissions verified all 1,241 records with no
drops and no changes to the original source files. Fourteen model tests pass.
The earlier investigation notes below describe the initial capture before this
producer update; their missing-impact limitation no longer applies to the new
capture. Complete AI census and dedicated multiplayer latency remain untested.

Open https://localhost:5081/playback after signing in with a Discord account carrying the configured Developer role. Playback also appears in Explore pages. Both the page and every recording/mission request enforce that role on the server.

Choose a closed recording, press Play, drag the timeline or click an activity event. Use player/AI/vehicle checkboxes and individual actor checkboxes. Select an actor to view stance, equipment and its recorded vehicle/seat. Pan by dragging and zoom with the wheel or buttons. Mission statistics are latest cumulative totals, not reconstructed totals at the playhead.

## Data truth and boundaries

- The first viewer loads a stable closed recording: at most 100,000 records, two hours and 32 MB of returned JSON. Larger recordings need a future indexed/windowed baseline API, not raised limits alone. Range requests remain bounded and cancellable. Missing batches block playback; explicit gaps/drops are marked degraded.
- Actor states are precomputed for binary-search seeking, including backward seeks, ownership, seats, death and despawn. Interpolation only joins adjacent continuous samples within one second and never crosses a recorded gap. Paths show only recorded samples in the previous 3 seconds. Last-known positions remain visible until despawn; absent telemetry is not proof of an actor's current state.
- Players are sampled regularly. Current Lobby2 captures AI incidentally during combat, not a complete AI census. Vehicle transforms and seat changes are supported, but this walking/shooting capture contains no vehicle entry; vehicle behavior has synthetic model coverage only.
- Shots now default to sampled projectile-position dots. Optional direction hints show 60-metre velocity rays; the arbitrary length is not flight distance or a confirmed landing. These sampled positions are not verified muzzle locations. Actual linked impact/detonation records are supported with endpoint markers. A matching projectile ID and nonzero damage position can produce a damage line, explicitly distinct from a terrain impact. Legacy damage zero vectors are treated as unset. No time/proximity matching invents projectile attribution.
- This capture has 59 shots, 26 damage events, three deaths and all three stance values. Its damage projectile IDs are empty, its hit positions are zero, and coverage explicitly says impacts=false. Exact bullet landings cannot be recovered from it. Future precise landing capture needs an authoritative engine collision hook and schema evidence; the UI already accepts those event types.
- Vehicle/AI names are readable prefab names, not a localization/asset-name catalogue. AI entity IDs distinguish otherwise identical units. Arma identities are not automatically matched to Discord accounts.
- Viewing does not change recordings, stats, Lobby2 files or server state. There are no browser-to-game calls or test-login bypasses in the shipped app.

## Arland map provenance

Game version 1.8.0.13; installed PAK catalogue revision 721cd7b05eb54d3ca3591d9097af381d. MapEntity_Arland.et points to UI/Textures/Map/worlds/Arland/ArlandRasterized.edds in data010.pak. Source SHA256 f4f090f2198396230d65df4b79a6f84080da6ba25c7d53ce999b4e120513c87f. The 4096x4096 image was decompressed from its EDDS mip blocks and converted to WebP for this local viewer. Game artwork remains Bohemia Interactive material.

A temporary read-only optional test-addon probe called SCR_MapEntity.GetMapInstance().Size()/Offset() through Reforger MCP in the loaded Arland_Lobby world. Returned size=<4096,311.375,4096>, offset=<0,-163,0> (operation 26b2c3c618c94f789614401c5cc2f538, 2026-11-02). Probe was removed; Reload returned Workbench to stopped Play. No production Lobby2 scripts changed. Existing ArlandLobby resource-GUID warnings remain unrelated.

SCR_MapEntity.c WorldToScreen (lines 898–941) maps X east/right and Z north/up. Thus the raster spans X/Z [0,0] to [4096,4096], with its top at Z4096. dist/playback-maps.json explicitly maps the tested world suffix to this asset. Unknown worlds use a labelled metric grid rather than an assumed raster. Add other maps only after verifying their image, dimensions and offsets.

## Checks

`node --test tests/playback.test.mjs` covers seek reversal, death/despawn, seats, shot evidence and discontinuities. If the private local exported capture exists under artifacts/playback-fixture, the same test additionally checks its 495 real records, 59 shots, three deaths and stance values. Capture files are ignored and are not shipped.

The .NET infrastructure test suite exercises anonymous/member denial, Developer access, permission-filtered navigation, recording list fields and pagination, using a disposable database. Browser testing uses the real captured data behind a temporary loopback-only fixture; no auth weakening is required on the website.

Final verification (2026-11-02): Release build passed with no warnings/errors; ten playback tests passed; database/authentication suite passed including recording directory pagination and Developer access. Browser verified real-capture event seeking, play/pause advancement, actor selection, all/none and player filtering; narrow viewport had no horizontal overflow. Running site's protected routes return 401 anonymously; map and module assets return 200. Independent review approved after regression fixes.

## Factions and shot-evidence follow-up

Faction filters are populated from recorded spawn/appearance states. US defaults blue, USSR red, FIA green, CIV purple; unknown or custom keys use neutral gray. Color pickers customize this visit only. These are viewer colors, not an authoritative game color table or inferred BLUFOR/OPFOR alliances. Current actor affiliation follows seek time; shot evidence retains firing-time affiliation. Circles, triangles and rectangles distinguish players, AI and vehicles regardless of color. The current real capture contains US only.

Producer investigation: GC_StatsCharacterTracker.c registers OnProjectileShot and passes the projectile to GC_TelemetryRecorder.RecordShot. That method reads projectile.GetOrigin() and movement velocity, without a muzzle position argument. RecordDamage reads damage.hitPosition and attempts ProjectileId(damage.damageSource,false); this capture has unset zero positions and empty projectile references. No exact firing-to-impact evidence can be reconstructed from those fields.

Installed game declarations (catalogue 721cd7b05eb54d3ca3591d9097af381d, ProjectileMoveComponent.c and BaseProjectileComponent.c) expose launch input, velocity and instigator/parent getters. This investigation did not establish an authoritative script callback covering every impact, including missed/non-damaging shots. Accurate launch and impact capture remains a separate server-side producer task: establish and exercise the owning callback, preserve projectile identity, record timestamp/world-position and evidence type, then prove known muzzle/target and missed-shot cases through the existing intake. Do not infer a landing from velocity, a nearby death, a zero hitPosition, or projectile disappearance.

Follow-up validation: twelve model tests pass, including faction change/backward seek and faction switch/despawn before a delayed impact. Browser checks verified recorded-US filtering, configurable color, and direction hints disabled by default. No production Lobby2 scripts were changed.

## Stationary units and player bodies

New Lobby2 captures discover existing AI from the native unit registry, so a
stationary unit needs only its initial spawn position to remain visible.
Unused playable slots are excluded until occupied. After ownership is cleared,
a surviving previously occupied body remains in the Players filter and displays
its unit label without the former occupant's name. Names in the actor list, map
and event log follow the selected/event time, including backward seeks.
Actual entity removal still ends the actor's presence.

The 2026-11-02 discovery capture (01a19e06-4852-8ff7-8400-0000006a8b6b) passed
the production intake and range APIs: 378 records, 18 AI, two player bodies and
one cleared-ownership event. Model/loader regressions: 19 tests passed.
Older recordings cannot recover units that were never captured.

## Facing and native icons (2026-11-02)

Facing uses recorded body rotation, not weapon aim: Enfusion GetAngles returns
XYZ rotations, so Y (index 1) is yaw; legacy transform forward is row 2.
North is up, east right. Heading interpolates along the shortest angular path
only across the same short continuous intervals as position. Missing headings
stay unknown. Map options toggle facing pointers and native icons independently.
Vehicle category symbols stay upright for readability; their pointers rotate.
Without icons, the simple vehicle rectangle rotates with its body heading.

New spawn records carry optional iconResource/iconName from the native
SCR_EditableEntityComponent.GetInfo() presentation. The viewer maps only known
resources and symbols to bundled local PNGs; arbitrary telemetry URLs are never
loaded. Unknown/custom resources keep generic markers. These describe the unit's
spawn role/type, not equipment picked up later. Older captures can show facing
but cannot supply missing role metadata.

Artwork provenance: installed game 1.8.0.13, catalogue
721cd7b05eb54d3ca3591d9097af381d, game/data/data010.pak.
EditableEntities.imageset SHA256
4df591f0b78b55abd0fa69309054efab0f6b4a55dd0d9b3371c8546bce795996;
EditableEntities_atlas.edds SHA256
8725685fe284d78bfe57775be5dc0037732742cfe1866193d04f040c7ff55a6c.
LoadoutIcons.imageset SHA256
7cfb58da2748bde0b9380a2ad16b61d43466988189151a0f557c2f7edc2d4c3f;
LoadoutIcons-200_atlas.edds SHA256
0b78ce98f9c639007fbd5795d7395a1993730f556a27c052d45b29f6c065593f.
Converted EDDS to DDS, cropped named cells (2x LoadoutIcons RefSize coordinates),
trimmed transparent padding, and preserved artwork in PNG. Direct native editor
texture names map to the matching named atlas symbol. Artwork remains Bohemia
Interactive material. No inferred vehicle classification or external image fetch.

Live proof: M998, UH1H and USSR LAT returned their native icon references; yaw90
returned forward(1,0,0), yaw270 forward(-1,0,0). Recording
01a19e0e-ba7e-8a6d-8400-0000006f4026 passed production intake/read APIs with
1,007 records including 47 vehicle samples, zero drops. Browser tested this
capture on desktop and narrow viewport, with both toggles and no script errors.
Twenty-four model/loader/icon tests passed, including north-wrap, reverse seek,
gaps, missing orientation, legacy vehicle matrices and untrusted icon resources.

## Recorded map layers

`map-state` is a complete replacement snapshot for one faction; the empty faction key is mission/public content. Sampling spreads factions across frames and checks again after one second. Unchanged snapshots are omitted; changes lasting less than a sampling interval may not be captured. Replay uses only the latest snapshot at or before the current time, including empty snapshots for deletion. Older recordings have no map layers.

Lobby2 reuses `GC_MapShapeComponent.AppendFactionSnapshot` for canonical enabled/faction filtering, polygon vertices, line/fill colors and widths, and inverted AO fills. Admin-only boundary hints are excluded. Authored/runtime briefing markers use their existing phase/faction visibility. Native shared custom and military markers come from copied static and disabled collections; client-local private markers are unavailable to the server. Marker positions, labels, rotation, sizing, native configuration/flags and military identity/dimension/color are preserved. Unsupported artwork is visibly reported and rendered with a generic symbol; military composite artwork is not yet reproduced.

MapDrawing support uses the existing optional server adapter. Packed point decoding, width, color, opacity and faction rules match installed `TILW_Drawing.c` and `TILW_DrawingHelper.c`. This adapter was source-checked, but the optional addon was not loaded for this live test. No client web calls were introduced.

Bounds: at most 64 factions, 384 shapes, 512 markers and 64 KiB per snapshot; overflow stops capture explicitly. Map records are not activity-log entries. The viewer offers independent Map faction, AO & drawings and Map markers controls.

Provenance: game1.8.0.13, catalogue721cd7b05eb54d3ca3591d9097af381d. `icons_mapMarkersUI.imageset` SHA25614f7ac4f8d8531d0694b9b44a360df54bdd8b3e771ba7b8be30b5ae339f68701; `icons_mapMarkersUI-200_atlas.edds` SHA2569f1a64db37c843727cdfcfacf56a6ba178be4bb4caeeac9bef346d08e77f659f. Both are in game/data/data010.pak/UI/Textures/Icons. Converted EDDS and cropped 91 named 2x atlas cells, preserving padding. Artwork remains Bohemia Interactive material.

Live proof: recording01a19e1e-6933-897d-8400-00000072245f, 9530 records, zero drops, 11 map snapshots, complete through production intake and read APIs. Public/US/USSR contents, concave polygon inversion, AO visibility and mission-marker deletion verified. Recording01a19e23-5486-8a30-8400-00000074f037 additionally proves native shared-marker deletion. Temporary markers were removed and map recovery is empty. Workbench compile passed; three existing Arland resource GUID errors remain. Browser checked desktop/mobile, faction layers, toggles, backward seek and continued playback. Optional MapDrawing and military symbol appearance remain outside this live proof.

Vehicle map symbols now use north-facing top-down silhouettes classified from recorded native vehicle icon metadata; their entire silhouette rotates with recorded yaw. These are viewer drawings, not exported native artwork. Unknown types use a generic vehicle silhouette. Recent paths show three seconds.

Voice telemetry (Lobby2.Contracts 1.1.2): while speaking, a white speaker or radio
glyph replaces the role artwork inside the faction-colored marker. Radio takes
priority if both channels are active; the role returns on stop or lease expiry.
Voice does not add noise to the combat activity log. Start/refresh/stop records
remain inspectable under Server data. These are client-reported capture signals,
not audio or proof of reception. Old recordings have no voice indicators.
The model reconstructs speech on seek and clears it on ownership changes/death.


## Terrain rendering and performance (November 2026)

Terrain is composited with WebGL 2 beneath the existing 2D actor, label and interaction overlay. An indexed worker rasterizes world-anchored 512-pixel tiles with 32-pixel filtering gutters. Resolution levels round up to the display's pixel density; moving or zooming no longer suspends detail generation until the gesture ends. Tiles remain cached across pans, visible work precedes adjacent-area prefetch, and only one worker job is dispatched per display update. Global contour-label layouts keep collision decisions consistent across tile boundaries.

The retained bitmap budget is 256 MiB, increased to fit the current visible working set on unusually large/HiDPI displays. Visible tiles are never evicted for prefetch. This is not a total browser-memory cap: GPU textures, source rasters and map geometry also occupy memory. Eviction releases both bitmap resources and, at the next draw, their GPU textures. Oversized source rasters are uploaded in native-resolution chunks with one-pixel sampling gutters; no source downsampling is introduced. Scenery visibility and opacity remain independent controls without rerasterizing terrain. GPU context restoration reuploads retained data. A graphics failure reports the unavailable state without stopping the playback animation loop.

Validation uses `node --test tests/playback*.test.mjs` and the local browser harnesses in `artifacts/preview-quality/`. `anizay-perf-warm.cjs` waits for all 154,682 Anizay features, all six natural rasters and the initial tile work to finish before moving the camera. The workload uses a real single-player recording; it does not establish performance for a large multiplayer recording. `anizay-tile-quality.cjs` checks layer controls, graphics-context restoration and fractional-scale chunk boundaries against an unsplit image.

Unvisited areas and newly requested zoom levels still need rendering; cached coverage remains visible while that work completes. Source raster detail itself is finite. These limitations are distinct from reducing resolution during dragging. Keep map-package presence, full-detail coverage, worst-frame timing and memory alongside average frame rate in future optimization checks. Package-side precomputed tiles are a possible next step only if profiling shows first-visit generation is a material bottleneck.
