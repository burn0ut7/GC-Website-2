# Manual Workbench map export and upload

The website consumes a portable `.zip` map package. Workbench and installed game/mod assets are needed only on the exporting computer. The website server only stores and serves the processed package. Arland's bundled reference assets remain unchanged.

## One-command workshop export

Run `Export-Map.cmd --map zarichne` from the website checkout. Object previews default to batches of 50. Use `--batch-size 20` to select smaller batches. Use another installed terrain's project name (lowercase slug) or workshop ID for the next map. Start with Workbench closed, on the requested terrain's idle resource browser, or in the generated export project with Play stopped. The command opens the project and world, starts Play, calibrates the map, captures the layers and previews, writes the ZIP, and stops Play. It closes a generated project that it launched, ready for the next command. It restores a terrain project it temporarily closed. An unrelated open project or a terrain project with an editor open must be closed normally first. The finished Zarichne package is `artifacts/workbench-maps/zarichne/zarichne.zip`.

For automatic discovery the installed addon must contain an unambiguous scenario with an explicit parent world, AI world prefab and map entity prefab. The map entity must directly reference the topography and satellite texture, with a single supported supertexture grid in the addon. For ambiguous scenario wiring, pass a JSON recipe with `name`, `slug`, `workshopId`, `parentWorld`, `aiWorld` and `mapEntity` (the three resources include their GUIDs). The existing explicit full-map recipes remain supported.

Set `missionRoots` in local settings to authored mission addon directories. Packages include recording aliases for worlds directly inheriting the captured terrain from these sources. Website builds supporting aliases expose those missions as separate registry entries sharing the same asset URLs. Set `workshopRoot` if the game addons live outside the sibling ArmaReforger profile. The generated fixture is isolated under Workbench addons and preserves modified files. Include additional loaded asset directories in `previewSourceRoots` for custom dependency overrides.

## Export on this computer

1. Open the map's Lobby2 test world in Workbench with `Lobby2McpTests` loaded. Start Play. Keep it running until export finishes. This is an offline preparation session, not a live community session.
2. Double-click `Export-Map.cmd` in the website checkout and enter `everon`, `kolguyev`, or `arland`. Alternatively run `Export-Map.cmd --map everon`.
3. The script reads the installed source textures/geometry, captures terrain heights, water depths, scenery, names and map symbols, then renders missing object-model previews. Unchanged models reuse the shared capture library across maps. It restores the previous camera after previews and does not save the world.
4. When it prints **Ready to upload**, take the resulting `<map>.zip`. Stop Play normally. Do not upload the source cache, Workbench profile or original game archives.

The local settings have been prepared in ignored `map-export.local.json`. On a different computer, copy `map-export.example.json` to that name and set its paths. Requirements: .NET 10 SDK, Python with `pip install -r scripts/map-requirements.txt`, Node with `npm install --prefix scripts`, and the existing shared Reforger MCP service configured for live Workbench calls when running the optional full export. The offline source extractor owns its PAK reader in this website project and builds independently. The script discovers the one running Workbench process, so no process ID needs to be entered.

Test worlds:

| Map | Workbench resource |
|---|---|
| Arland | `$Lobby2:worlds/examples/arland/Arland_Lobby.ent` |
| Everon | `$Lobby2:worlds/examples/eden/Eden_Lobby.ent` |
| Kolguyev | `$Lobby2McpTests:worlds/playback/Cain_Playback.ent` |

Large maps take several minutes. Scenery capture can temporarily occupy the editor while it reads more than a million objects. Progress and failures are printed. A failed export does not update the website. Restart Play before a fresh full export; the explicit export session locks its world and map name. Low-level capture/build phases can be resumed deliberately using `workbench-map-capture.py` and `build-workbench-map.py`; never rerun a timed-out mutation without checking its status/output first.

## Upload to the website

1. Sign in with a Discord account that has the configured **Developer** role.
2. Open **Administration → Playback maps**.
3. Choose the exported ZIP and click **Upload map**. The page shows upload progress, then validation/install status.
4. Reload playback. The map is discovered from the server registry, including all package-local layers and model images.

Uploading the same map ID activates the new version atomically. Existing playback tabs can continue using the old immutable asset URLs; old revisions are retained. Failed validation leaves the active map unchanged. No deployment or server rebuild is required for later map imports.

Set `Maps:StoragePath` in the server's private configuration to a persistent directory outside the deployment folder. The default is `%LOCALAPPDATA%/GCWebsite/Maps` for the website service account. Back up that directory, including `active.json`. This initial implementation assumes one website process owns the directory; it is not a shared multi-server registry. Old revisions are intentionally not automatically deleted.

## Package contract

Building previews are shared across maps. The capture library lives under the Workbench profile in `GC_MapPreviewLibrary`; it uses the source resource plus a content revision of the configured installed assets and capture recipe. Captures resolve authored MeshObject resources from the source prefabs, keeping placed child transforms from the loaded world into temporary full-detail batches of up to 50. Models are prepared in a compact row. Only the selected model is visible, and the camera clips just beyond its bounds. Authored material remaps are preserved. The camera requests engine preloading and screenshot capture waits for completion. This recipe invalidates earlier previews. The studio sits at 2,048 m elevation or 1,024 m above the terrain maximum, whichever is higher. The camera fits the rendered bounds within the central square used by the website. Combined-preview cache identities include each member resource and relative transform. Gameplay components and editor splines are not instantiated. Missing images are captured automatically; complete PNG validation prevents interrupted screenshots from entering the library. Set `previewSourceRoots` in local export settings to all game and mod asset roots loaded in Workbench, including loose mod sources; it defaults to `pakRoots`. Keep that list complete when testing overrides. Changed source content conservatively invalidates the library, while switching maps with the same asset set reuses it. Export an untouched offline world: these are model previews, and an intentionally damaged or altered source instance can affect its visual snapshot.

Portable ZIPs include their required previews so they work on a new server. Import stores identical images only once in the server's `previews` directory and rewrites references to authenticated `/map-previews/<content-hash>.webp` URLs. Previously installed immutable revisions are retained; uploading a new package does not delete old versions.

`manifest.json` contains `format: "gc-map"`, `version: 1`, a lowercase map `id`, the calibrated `map` configuration, and `files` with exact case-sensitive paths, sizes and SHA-256 hashes. Internal references start with `@map/`; installation rewrites them to immutable `/map-packages/<id>/<revision>/...` URLs. Only JSON, PNG and WebP assets are accepted. Source archives, executable content and credentials do not belong in a package.

Limits: 256 MB upload, 512 MB expanded, 10,000 entries, 64 MB per asset, image dimensions at most 8192 per side. Terrain details are divided into parts below 2 MB for the existing bounded playback loader. The importer verifies hashes, rejects unsafe paths/duplicate entries/external asset references and requires both Developer authorization and a CSRF token.

Included: native raster, ground-color relief, vegetation/rocks, unified water/depth shading, roads/buildings/power lines/contours, localized place names, native facility icons and exact-prefab building preview images. The exporter considers every resource-bearing world entity, without a model-name whitelist, and includes both native building and wall footprints. Matched footprints carry their model references into the rendered map. The native TOPO format does not carry source prefab IDs, so the initial association still uses spatial bounds: unresolved footprints are listed in artifacts/map-tools/<map>-unresolved-objects.json; overlapping candidates are captured together at their exported relative transforms as one preview, rather than a model dropdown. The association remains spatial and must not be described as an exact native source identity. Old multi-candidate packages need regeneration to supply a combined image. The compact preview catalog stores unique models, not a second copy of all geometry. Provenance records the source and height hashes, world identity and resolution.

## Adding another terrain

Create a recipe based on `scripts/maps/everon.json`, with the real world suffix, calibrated bounds, original TOPO hash, raster resource and supertexture tile prefix/count. Supply the intended installed PAK roots in local settings. Use `Export-Map.cmd --recipe path/to/custom-map.json`. The exporter rejects ambiguous duplicate asset overrides rather than silently choosing the wrong mod version.

The current verified format supports zero-origin square terrains, dimensions divisible by 8, the native TOPO layouts checked by the parser, and 256-pixel terrain tiles with the verified repeated 4-pixel border. Unsupported custom layouts require inspection and an explicit importer change; this is not a claim that every mod format already works. Custom map-symbol types absent from the installed baseline style table are reported as unmapped, never invented.

Playback matches the exported scenario's world suffix. A different mission path on the same island needs an explicit matching map entry; it is not guessed from the mission name. Automatic terrain identification from future recordings is separate work.

Everon/Kolguyev use 8 m height samples, real 4 m ground-color averages and 4096-pixel scenery/water layers (3.125 m per pixel). Very narrow water features can be smaller than this grid. Arland retains its existing 1 m layer grid. All water depths come from the engine's terrain and water surfaces; vegetation is layered above water and submerged scenery fades by its measured depth.

The file-only terrain reconstruction experiment is not used for water, heights or scene classification. Reading source texture/TOPO bytes is still part of preparation, while Workbench remains the authority for resolved world data.


## Anizay installed-mod example

Anizay 1.0.12 (workshop ID `6266DCA9193C705E`) was exported from installed
files and live Workbench, at verified bounds `[0,0,10240,10240]`. Its 1600
128px ground tiles use the same four-pixel repeated border as the base maps;
the exporter normalizes them to the established 256m tile grid.

Open Lobby2's optional `tests/AnizayMapExport/addon.gproj`, with Anizay and
Lobby2McpTests discoverable under Workbench addons. It depends on those addons
without adding Anizay to production Lobby2. Open
`$GCAnizayMapExport:worlds/playback/Anizay_Playback.ent` and start Play.
Use `scripts/maps/anizay.json` with `scripts/export-map.py --map anizay` and
local settings whose `pakRoots` include the base game and installed Anizay
folder. Include all loaded asset roots in `previewSourceRoots`.
Upload the resulting `anizay.zip` in Administration > Playback maps.

This package currently matches the explicit `worlds/playback/anizay_playback.ent`
recording suffix. Another mission on the same terrain needs its world identity
registered as well; terrain inheritance is not automatically inferred from
recording paths.

Validation: native height/scenery/water capture, 222 preview images, six layers,
74 labels, 90 facility symbols, and an imported 85-second recording with two
actors, 100 records, all three stances, and zero dropped records. Browser checks
loaded the imported package and advanced playback. This was a movement/stance
fixture, not a multiplayer combat test.

## Quick archive-only fallback

Run `Export-Map-Fallback.cmd --map arland` (also everon, kolguyev, anizay),
or `--recipe path/to/map.json` for another map. No Workbench, MCP connection,
ray tracing, scenery export, or screenshots are involved. It reads only the
recipe's existing EDDS map texture from installed PAKs using the shared archive
reader, converts it, and produces a small portable ZIP. Upload that ZIP through
Administration > Playback maps. Imported packages can be downloaded there too;
older packages imported before ZIP retention need reimporting to enable download.

Uses the same local settings, .NET/Python/Node dependencies and installed PAK
roots as the full exporter, but no running editor. Minimal recipes need `name`,
`slug`, `worldSuffix`, `raster`, `bounds`, and `calibration`. The existing recipes
already supply these. Game MapEntity prefabs identify the raster resource;
unknown mission/terrain inheritance and bounds are not guessed from names or
image pixel counts. Supply a verified recipe for an unfamiliar mod map.

This is the game's authored map raster (which may be cartographic rather than
satellite imagery). It remains marked Basic map only and stays on the admin
attention list. A detailed map always takes priority; a fallback with identical
world bounds is retained if its main background fails to load. Different bounds
are never attached to a detailed map. This first slice is an explicit archive
import, not a background scan of arbitrary newly installed mods.


### Deep inland water

For each native water footprint pixel, Workbench probes downward from the
capture ceiling to the live terrain height in 0.5 m steps. The ceiling uses
the map's upper elevation plus a 512 m margin. The first engine-confirmed
water surface above the bed sets the depth, regardless of the water volume's
thickness relative to the bed. The final sample is taken at the terrain
height so the scan covers the shallow edge as well.

Re-export the water phase to update existing maps. Water bodies outside
the native footprints and volumes narrower than the sampling interval can
still be missed.
