# Arland topographical detail

For new full exports and server uploads, use [Manual Workbench map export](manual-map-import.md).
The commands below document the original Arland reference build; the current exporter
requires an explicitly configured session before capturing building previews.

Playback now draws static vector roads, runway surfaces, building footprints, walls, power lines and zoom-adaptive 5/10/20-metre contours over the existing calibrated raster. Controls are under **Map layers > Terrain detail**. The vectors remain sharp when zoomed. Drawing is cached until the viewport or selected layers change; terrain loading does not block recording playback.

## Provenance and calibration

- Installed Arma Reforger 1.8.0.13: `game/data/data010.pak/worlds/Arland/Arland.topo`.
- TOPO SHA-256: `479a06286587a076a0dd83e5c5e0a78154d81c8a6f9a248a453ad43ca56cf0ec`.
- Terrain grid SHA-256: `06fe1eb785f8661a638a2c86b7b2bd4065554383c3818a6b0507ef82c4447da1`.
- Terrain was sampled in the live Arland Lobby world with `GetSurfaceY` at 8m intervals (513 by 513). The optional, admin-gated `GC_TopographyExportTests.c` helper lives only in Lobby2's MCP test addon. It is an explicit offline export, never a mission recorder task.
- Map extent: X/Z 0–4096m. Native polygon Y increases southward; convert with world Z = 4096 - Y. First building polygon centre (2257.6465, 1570.4696) agrees with the native world-space building-symbol anchor. The rendered airfield and island roads also align with the calibrated underlay.
- Native TOPO interpretation is inferred from installed Workbench loader code, not a published file-format specification: root loader `FUN_140a5a4b0`, chunk reader `FUN_140cf3b60`, roads `FUN_140cf8670`, buildings `FUN_140cfc6e0`, power lines `FUN_140cfa550`. Executable SHA-256 `7dd36ca1f662b0c13669a4f2f7f10e3c461c2906b021e742272d64b3ab7e9798`; analysis key `556b45753ce44fd47a5cd944d3546607e0ff2727068bb051fc1dff6a737d4d23`. Analysis had warnings/errors; this is not original source. The builder pins the exact verified TOPO hash rather than guessing other layouts.
- Roads use highest-detail native geometry: 125 objects, represented as 5,892 quad pieces. Each four-corner group is ordered around its convex hull; auxiliary section offsets are not triangle indices. Buildings: 348 footprints; walls: 7,322; power lines: 32; contours: 151 polylines.

Bohemia's [2D map creation documentation](https://community.bohemia.net/wiki/Arma_Reforger%3A2D_Map_Creation) describes separate raster and topography exports. We use the installed topography rather than estimating roads from image pixels.

## Rebuild

Install `scripts/map-requirements.txt` in a build-only Python environment, then run:

```text
python scripts/build-arland-map.py <installed-Arland.topo-export> <GC_MapDetail_Arland.json> dist/maps/arland-detail.json
```

The source files stay outside the public website. The generated compact JSON is about 1.67 MB; normal website operation needs no Python or game-file access. Add another world only with its own verified geometry, terrain export and calibration in `playback-maps.json`. Unknown worlds explicitly report unavailable detail.

## Scope

This is static Arland cartography, not every rendered object. The original raster retains vegetation and water; the optional natural terrain/scenery layers below replace that background. Small props, mission-added structures and destruction are not reconstructed. Contours interpolate an 8m grid, not a survey-quality terrain mesh. Historical recordings use this static map revision; actor positions and mission drawings still come from their own recorded data. Walls and power lines appear only when zoomed in enough to remain readable. Re-export after a relevant map revision; do not silently interpret a different TOPO binary.

## Verification

Live terrain export returned 263,169 samples. Browser validation used 1,007 real captured telemetry records: playback continued with all layer switches exercised, no JavaScript errors, and no horizontal overflow at 540px. Node tests verify geometry counts, north-up building calibration, malformed input rejection, viewport culling and layer selection. Whole-map screenshot: local development artifact `artifacts/icons-proof/topography.png`.

### Base-game palette

Building and road colors now come directly from `game/data/data007.pak/Configs/Map/MapPropsDefault.conf` (SHA-256 `c4f6c4c1836feb2a3cafc6361d29be3103ce0d31353ced2b6e9d09ebbcbae23c`, lines 3–27 and 46–101, catalogue revision above). RGB components are mapped to CSS percentages. Buildings use 0.456 gray with 0.205 outlines; walls/castle structures use 0.204 gray. Roads retain separate runway, main, minor, trail, tourist-path and general-path colors. Bridge fill uses the white default in `SCR_RoadTypeProps` (SCR_MapPropsConfig.c lines 301–302). Walls have no outline, matching the resource. This change matches configured colors, not all native road dash, border and zoom behavior; geometry and existing playback visibility rules are retained.

## Natural terrain and individual scenery

`build-arland-natural.py` generates our own relief image and three independent alpha layers at 1m/pixel (4096 square). Ground color now comes from the installed terrain supertextures (see source-color section below). The earlier illustrative earth/stone slope palette has been removed; the measured 8m grid contributes neutral brightness shading and a water mask only. This replaces the raster when **Shaded terrain** is enabled; disabling it restores the original game raster. Buildings and roads retain the game's configured palette over either ground background; translucent scenery renders above water and terrain geometry, with symbols and playback actors above it.

The optional Lobby2 `GC_SceneryExportTests.c` helper queries the loaded Arland world once using `QueryEntitiesByAABB` and exports each Tree's `TreeClass.SoundType` and world bounds. Bush types 1, 7, 8, 9 are separate; the remaining Tree types (including withered trees/stumps) use the trees layer. Rock prefabs are identified by `/Rocks/` in their resource path. This does not establish coverage of every possible modded object or procedural grass. Live result: 151,749 exported entries, split into 73,565 trees, 44,495 bushes and 33,689 rocks. Query completed in the current saved Arland Lobby world; normal gameplay never runs the export. The resource paths remain in the local export, not public assets.

Each footprint is a soft ellipse using its measured world-axis bounds, not an exact canopy or rock mesh silhouette. Optical depth accumulates per object, giving conventional alpha-over composition: two 30% tree stamps produce 51% opacity. No random or fabricated plants are placed. Some rock objects extend offshore/below the water; the static footprint layer is not an above-water visibility simulation. Current tree state is a snapshot, not historical destruction. Per-layer switches and a scenery-opacity slider keep density adjustable independently of roads/actors.

Generated files are `arland-relief.webp`, `arland-trees.webp`, `arland-bushes.webp`, `arland-rocks.webp`; total about 3.7MB. `arland-natural-source.json` retains hashes, counts, spacing and bounds. Images load independently without blocking telemetry playback. Existing selection cancellation prevents old loads being applied to a different recording.

```text
python scripts/build-arland-natural.py <GC_MapDetail_Arland.json> <GC_MapScenery_Arland.tsv> dist/maps <arland-ground.png>
python tests/natural_map_test.py
```

Research inspiration: [TilW's Enhanced Map Tool guide](https://github.com/Til-Weimann/tilw-terrain-tools/wiki/Enhanced-Map-Tool) and [Enhanced Maps](https://reforger.armaplatform.com/workshop/644B042109700804). Their workflow combines exported heights with individual plant positions. No reference-mod textures or code were copied. Installed Tree export tool was inspected for API evidence only: `TILW_TreeExportTool.c` SHA-256 `0d42ec8864f80371ced7edae95786fa3b535f58924a0e2922a1b2c3f8d285c25`, lines 47–61 and 81–87. Installed native declarations: BaseWorld.QueryEntitiesByAABB, IEntity.GetWorldBounds, ETreeSoundTypes. The website implementation is independently authored.

Validation: live export returned the stated count; helper compiled with no script errors (three unrelated pre-existing resource-GUID errors remain). Four Python checks cover overlapping opacity, north-up coordinates, bush classification and source-color preservation; all 31 playback tests pass. Browser fixture with real recorded telemetry exercised every new checkbox, continued playback, and reported no JS errors or 540px horizontal overflow.

## Towns and named places

Place labels are exported from the live Arland world using the optional `GC_MapLabelExportTests.c` helper: `MapDescriptorComponent.GetBaseType()` selects the `MDT_NAME_GENERIC` through `MDT_NAME_VALLEY` cartographic descriptors, `Item().GetDisplayName()` supplies their text, and `WidgetManager.Translate()` resolves localization in the current game language (English in this export). Coordinates come from the descriptor owner entity. This exported 29 labels, including Beauregard, Arleville, hills, bays, harbors and military sites. Unit names and mission markers are excluded. These are the world's authored names, not a complete inventory of unnamed facilities or campaign objectives.

Convert with `python scripts/build-arland-labels.py <GC_MapLabels_Arland.tsv> dist/maps/arland-labels.json`. The asset retains the source hash. On playback, **Place names** toggles the layer. Towns have priority when text overlaps, minor POIs appear at closer zoom, and a light halo keeps text readable. Labels render below recorded mission layers and actors. Names load separately without delaying playback. Validation: live export, successful script compile (existing resource warnings unchanged), browser screenshot and a Node test covering ordering, collision suppression, coordinate orientation, zoom and invalid input.

### Continuous road rendering

Road quads of the same type are now combined into one Canvas path per visible draw before filling. This removes internal anti-alias seams without expanding geometry or painting outlines over joints. Contours remain underneath; buildings/walls remain above. Verified against actual road geometry in a before/after browser screenshot and with a diagonal shared-edge pixel check at scales 0.5, 1, 3, 20 and device pixel ratios 1 and 2 (no interior color variation). All 32 playback tests and the Release build passed.


## Actual terrain colors (replaces the illustrative palette)

The tile assembly and compression described below were subsequently corrected; see **Terrain artifact correction and inland water** for the current pipeline.

Arland supplies 256 `worlds/Arland/Terrain/.Data/Terrain_N_supertexture.edds` tiles, each 256x256, in data010.pak. These are the terrain macro-color textures, not the stylized paper map. Original bytes were exported using Reforger MCP; the decoder verifies each original SHA-256, all 256 unique IDs, and DDS DXGI 99 (BC7_UNORM_SRGB). No additional gamma conversion or invented ground palette is applied. The RGB data assembles into a 4096px color atlas: indices advance eastward, then northward, and the full atlas is vertically flipped for a north-up display. The continuous airfield, roads and coastline corroborate the arrangement against existing world-coordinate geometry. The decoded atlas includes seabed; underwater pixels are replaced by the calibrated original game's map-water pixels using measured terrain height, not a new ocean color ramp.

`arland-ground-source.json` retains the original archive hash and all tile resource paths/hashes, with no local machine paths. `arland-natural-source.json` records the assembled color atlas and water-map hashes. Shorelines retain the actual gray terrain texture where above water. Relief changes brightness only; flat ground preserves source RGB before quality-92 WebP compression. The image is still cartography based on terrain macro-color, not a screenshot of real-time weather, lighting, water or high-frequency surface-detail textures.

Build-only decoder: `@bis-toolkit/edds` 1.0.2 (already used for the original map conversion), with Pillow decoding its reconstructed DDS. Install under `artifacts/map-tools`; Python requirements remain in `scripts/map-requirements.txt`. A local MCP export manifest contains original entry IDs, hashes and file paths; keep this local. Rebuild:

```text
node scripts/decode-arland-terrain.mjs <local-export-manifest.json> artifacts/map-tools/terrain-dds
python scripts/build-arland-ground.py artifacts/map-tools/terrain-dds artifacts/map-tools/arland-ground.png
python scripts/build-arland-natural.py <GC_MapDetail_Arland.json> <GC_MapScenery_Arland.tsv> dist/maps artifacts/map-tools/arland-ground.png
```

Validation: all 256 tiles decoded with verified source hashes and sRGB format; assembled coastline/airfield alignment visually inspected; four Python tests include exact source-color preservation on flat ground and game-map water selection. Existing vegetation and rock positions remain unchanged. Browser playback still works with the original base image unavailable.

## Native facility and landmark symbols

The static Arland symbol layer is exported from actual loaded objects' `MapDescriptorComponent.GetBaseType()` and owner transforms by the optional `GC_MapSymbolExportTests.c` helper. It does not infer facilities from object names, generate locations, or add gameplay objects. The export includes the game's cartographic object types, excluding unit/mission types and the separate place-name descriptors. Live export returned 80 positions across 14 types: 20 bunkers, 14 calvaries, 1 chapel, 2 churches, 6 fuel-station descriptors, 1 hospital, 4 lighthouses, 2 pubs, 3 springs, 4 towers, 2 transformers, 2 transmitters, 6 lookout towers and 13 water pumps. Counts are object descriptors, not necessarily unique facility sites.

`build-arland-symbols.py` maps these types through the installed `Configs/Map/MapDescriptorDefaults.conf` to the exact named rectangles in `UI/Textures/Map/topographicIcons/icons_topographic_map.imageset`. It crops the decoded atlas and applies the configured RGBA multiplication. The source atlas is `icons_topographic_map_atlasSRGB.edds`, SHA-256 `ae18e0cc24551e96c3129420800eb324aec978628e0dfcc2fc32471c1d6e997a`. Defaults SHA-256 `e885b63cc9aedd31203a4b5c648274c13fb9d5892f839fba5dcc819ab87a6c97`; imageset SHA-256 `b617e5b576e726767d7ae0c198d90f81418a50c108f36198e3d9ab05d33adee9`. Source hashes are retained in `arland-symbols.json`; all 80 exported positions mapped successfully. Only generated, allowlisted local icon URLs are loaded.

Native `m_bAlignWithParent` drives rotation using recorded object XYZ yaw, and configured `m_iViewLayer` preserves the visibility hierarchy. Website pixel sizes and zoom thresholds are adapted to this viewer, not a byte-for-byte reproduction of the game's UI scaling or aggregation. These remain a static world snapshot; destruction and dynamically spawned mission facilities are not time-replayed. Landmarks & facilities is separately toggleable from unit icons and place names. It is drawn below names, mission drawings and actors.

Rebuild: `python scripts/build-arland-symbols.py <GC_MapSymbols_Arland.tsv> <MapDescriptorDefaults.conf> <MapDescriptorVisibilityDefault.conf> <icons_topographic_map.imageset> <decoded-atlas.dds> dist`. Atlas EDDS mip decompression uses the same existing `@bis-toolkit/edds` conversion as other shipped game symbols. The decoder invocation for this snapshot is in the local build artifacts. Validation: helper compiled and executed in Workbench (no script errors, existing resource GUID errors unchanged); all 80 asset mappings verified; renderer tests check position, yaw, culling and visibility; browser screenshot confirms symbols overlay the calibrated map.

## Terrain artifact correction and inland water

The initial flat water rendering described in this section is superseded by **Unified depth-shaded water** below.

The prominent grid-like texture streaks were already present in the decoded, uncompressed atlas: four repeated rows/columns at each tile edge had been stitched into the world. Inspection of all 256 installed tiles found those edge blocks repeat within less than 0.1 mean 8-bit channel error (BC7 introduces small differences). `build-arland-ground.py` now validates that observed layout, removes four pixels per side, and maps each remaining interior back onto its 256m cell. This is an empirically verified correction for this installed dataset, not a claim to have recovered the engine's shader UV implementation. Unsupported border layouts fail explicitly. Close-up before/after artifacts are `artifacts/map-tools/ground-crop.png` and `ground-crop-fixed.png`.

After assembly, source colors are averaged over 4m cells in linear light and interpolated across the complete atlas. This reduces high-frequency noise without substituting a ground palette or filtering each tile independently. These remain terrain macro-texture colors, not screenshots and not a reconstruction of individual terrain-material blend weights. The optional neutral hillshade is unchanged. Relief now uses lossless WebP so another lossy encoding cannot introduce new block artifacts. `CELL_METRES` in the ground builder controls the averaging scale; regenerate ground then natural layers using the commands above.

Inland water comes from the existing, hash-pinned `Arland.topo` **WATR v1** chunk at offset 1417590 (end 1446046). Inspection shows six detail levels; each record has a uint32 surface type, zero byte flag, counted float2 vertices and counted uint32 triangle indices. The parser validates every level and consumes the entire chunk; only level zero is rendered. Installed `EWaterSurfaceType.c` defines 2 as pond and 3 as river (SHA-256 `85bff2624454f0d5534163f59eec64a2fba6b1e4fdc64712883fdd56fe42da64`). The asset contains 3 pond meshes and 11 river sections, totaling 520 triangles. These are authored cartographic meshes, not newly placed objects or a list of distinct named rivers. Map coordinates use the same north-up conversion as roads/buildings. There are no guessed water polygons or height-only inland-water assumptions.

The **Inland water** checkbox controls these meshes. Water uses `MapPropsDefault.conf` line 130 `m_LandWaterColor 0.462 0.888 0.982 1`, drawn over terrain/scenery and contours, beneath roads/buildings/actors. Triangles are batched into one fill to avoid internal seams. Ocean rendering still uses the original map raster and terrain sea-level mask; this does not change tides, coast accuracy, or mission-added water. Rebuild map vectors with the existing `build-arland-map.py` command.

Validation: 8 Python tests (including border removal/rejection, linear-light averaging and solid-color preservation); 35 playback tests; real-recording browser checks for the water switch, continued playback, mobile width and eight water seam checks across zoom/DPR combinations; no browser errors. The Release build passes with zero warnings/errors. Visual evidence: `artifacts/icons-proof/inland-water.png` and `natural-closeup.png`.

## Unified depth-shaded water

Ocean, ponds and rivers now use one continuous coverage/depth field before coloring. `map-water.py` owns the shared light-to-dark presentation palette at depths 0, 2, 8, 30 and 100 metres (saturating beyond 100m). These colors are a requested cartographic style, not the game's physical water appearance. Ocean depth is sea level zero minus measured terrain height. The 8m vertex grid is interpolated bicubically onto 1m pixel centres with its half-cell alignment explicitly accounted for.

The optional `GC_WaterDepthExportTests.c` helper samples `ChimeraWorldUtils.TryGetWaterSurface` at the actual `GetSurfaceY` for each 1m pixel centre inside the native WATR footprint. It requires the current Lobby2 manager, server authority, an authenticated admin and the Arland Lobby world; no automatic hooks or gameplay changes. Live export: 29,059 probes; 7,771 submerged pond samples, 3,666 river samples and 290 ocean samples. One zero-depth river sample is omitted from visible coverage. Maximum measured inland depth is about 4.39m. Most of the authored mesh is under dry terrain; rejected/dry or surface-at/below-ground samples are transparent rather than painted as water. Comparing a mirrored-Z probe rejected that alternative coordinate interpretation; the original north-up conversion is retained. Export source hash is in `arland-water.json`.

Coverage is combined with ocean coverage, then smoothed over 1.2m (Gaussian sigma). Depth uses the same normalized filter over wet samples so banks don't bias the color. This intentionally softens the shoreline by a few metres for readability; it is not a survey or a source of numeric depth readouts. Rivers join the ocean in the same field, not as a separately colored patch. Both all-water and ocean-only variants are generated for the existing Inland water switch. The renderer draws this unified layer after contours/scenery and before roads/buildings, removes the redundant zero-elevation coastline, and no longer clips the smoothed image to the original jagged triangles. Land relief no longer contains baked water. If depth images are unavailable, the original native-water geometry remains a visibly reported loading/error fallback.

```text
python scripts/build-arland-water.py probes dist/maps/arland-detail.json <profile>/GC_WaterProbePoints.tsv
# In embedded Play, invoke GameMode_Lobby2 / GC_LobbyManager / GC_TestWaterDepthExport via Reforger MCP.
python scripts/build-arland-water.py build dist/maps/arland-detail.json <GC_MapDetail_Arland.json> <GC_WaterDepth_Arland.tsv> dist/maps/arland-water.webp
python scripts/build-arland-natural.py <GC_MapDetail_Arland.json> <GC_MapScenery_Arland.tsv> dist/maps artifacts/map-tools/arland-ground.png
```

Validation: helper reload compiled without script errors (the three pre-existing resource-GUID errors remain); authenticated live export returned its sample counts in 353ms and audit output. Workbench was returned to stopped Play in the same world/project. Eleven Python tests cover dry banks, softened coverage, depth transitions, missing samples, north-up vertex calibration, palette continuity and terrain preservation; playback/browser checks cover toggles, drawing, mobile width and continued playback. No standalone server or logged-out runtime test was added for this explicit offline-only helper; it reuses the existing production authorization gate.

### Scenery above and below water

The explicit Workbench scenery export now includes world-bounds bottom/top heights and water depth over each object's top, measured at its bounds centre with `TryGetWaterSurface`. Emergent reeds, bushes, trees and rocks retain normal opacity. Fully submerged tops fade by `exp(-depth / 2 metres)` before overlapping footprints are accumulated. The scenery layers render above water and terrain geometry, below symbols and playback actors. This is a static top-down bounds approximation, not a mesh cross-section: partially submerged crowns retain their footprint, and wave height is sampled once.

The fresh Arland export contains 151,749 objects, including 8,016 submerged rocks and 43 other submerged objects. Both current exporters retain existing server/current-manager/world/admin checks. Workbench reload passed with no script errors (three pre-existing resource GUID errors remain); the live scenery invocation returned `exported=151749`. Regenerate scenery before running `build-arland-natural.py`; older heightless exports are deliberately rejected.

Shoreline investigation at X1967.63 Z1764.73: live terrain Y=-0.224533, water Y=-0.0129623, ocean type, wet=true. The current generated water pixel is RGBA(165,222,232,255). Native GetSurfaceY analysis reads the terrain heightfield, not vegetation collisions. No shoreline expansion or plant-dependent water masking was introduced. The 8m coastal terrain grid remains an approximation between samples.

### Power-line visibility

Power lines are enabled by default and drawn in their own cached layer above scenery. The 32 routes remain the installed Arland TOPO geometry (six high-voltage, 26 low-voltage); no routes are invented. Solid RGB 0.082 strokes and widths 2.2/1.9/1.6 follow `game/data/data007.pak/Configs/Map/MapPropsDefault.conf` lines104–126 and `scripts/Game/Map/SCR_MapPropsConfig.c` lines373–397 (default very-high-voltage width). Enum order is verified in `scripts/Game/generated/Map/EMapPowerlineType.c` lines12–17. Source catalogue revision remains 721cd7b05eb54d3ca3591d9097af381d.

Web zoom thresholds .0625/.125/.25 pixels per metre preserve the native visibility ordering, not an exact equivalence with the game's discrete map layers. Widths are kept in screen pixels so routes remain readable. This slice draws the route strokes; native support/pole symbols are not reconstructed. Layer toggling, voltage widths/zoom ordering, browser rendering and uninterrupted playback were checked.


### Adaptive contours

Contour display follows USGS topographic conventions: brown intermediate lines, a heavier index line every fifth interval, and numerical index elevations. Reference: https://www.usgs.gov/ngp-standards-and-specifications/us-topo-cartographic-specifications-elevation . This is a convention-based game map, not a claim of compliance with every USGS cartographic specification.

The unchanged live 8m terrain grid now generates 5m vertical isolines (151 paths). Web zoom selects 20m intervals below .35 pixels/metre, 10m below 1.2, and 5m at closer scales; corresponding index intervals are100m,50m,25m. The key reports the active interval and metre units. At user preference, cool-gray strokes replace the conventional brown to distinguish contours from roads, retaining .65/1.5px weights. Index labels follow the local line direction with a dark halo. Candidates are spaced1280screen pixels along each line (previously320), with a360pixel initial offset and180pixel collision spacing to keep elevations sparse. Their heights are game-world elevations. This changes display detail, not source measurement resolution; isolines are not moved by decorative smoothing. Depression hachures and survey-quality spot elevations are not implemented.

### Rendering performance

Terrain painting now runs in a module Web Worker using OffscreenCanvas. The browser interaction thread only composites ready bitmaps and draws live overlays. One render may be in flight, with one replaceable latest request; old scene generations are discarded and their bitmaps closed. A1536px whole-map overview covers newly exposed areas while a detailed viewport plus384CSS-pixel margin renders. Detail is requested ahead of the edge at160px displacement. Zoom uses temporary .75DPR detail, then restores full device resolution after settling. The source geometry and imagery are unchanged. No backend change was needed.

The previous synchronous viewport cache still paused on large pans: a4K240-frame out-and-back benchmark measured main-thread draw p99237ms/max253ms and frame p99239ms/max254ms. With actual worker overview/detail verified, draw p990.5ms/max0.6ms and frame p9939ms/max55ms. These are synthetic headless Edge measurements on this machine, not a guaranteed display frame rate. GPU/compositor spikes can remain. Source/benchmarks are artifacts/icons-proof/playback-before-worker.js, large-pan.cjs, large-pan-before.json and large-pan-worker.json. worker-check.cjs covers long pans, rapid zoom/full-DPR settling, scene invalidation, resize and ongoing playback; scheduler tests cover bounded queues and stale-image disposal. Unsupported worker environments report rendering unavailable rather than silently using the old blocking renderer.

### Subdued textured contours prototype

The current presentation supersedes the earlier gray treatment. Brown contour/index hierarchy follows the USGS map-symbol guide: https://www.usgs.gov/ngp-standards-and-specifications/us-topo-map-symbol-guide . The zoom fade is a web presentation choice, not a claimed cartographic standard. Textured contours disappear at<=.45pixels/metre and smoothly reach full opacity at1.6; thin index/intermediate strokes are .9/.5CSS pixels at alpha .575/.299 (15% more visible after user feedback). Labels appear only at close scales and remain sparse. Both lines and labels are painted beneath roads, buildings and scenery. The non-textured presentation retains stronger brown contours. The existing checkbox disables both. An independent visual reviewer approved the fading prototype for default-enabled use; user preference remains open.

### Building model inspector

Click a visible building footprint to open a model preview; actor clicks take priority. The selected footprint is outlined, Escape/Close dismisses the card, and dragging never opens it. Catalog and images load on demand outside the rendering loop. Unsupported maps, ambiguous footprints and missing images report unavailable rather than substituting a nearby building.

`GC_BuildingExportTests.c` in the optional Lobby2 MCP test addon exports actual Arland structure prefab resources and world bounds (server/current-mode/admin guarded). `scripts/build-arland-buildings.py <Workbench profile>` associates the installed TOPO footprints by bounding-box intersection-over-union >=0.65, requiring a >=0.20 margin over the runner-up. This is conservative spatial association, not a native shared entity identifier. Exact footprint coordinates bind the web lookup; changed footprints require regeneration. Export/detail SHA256 values accompany the catalog. Current export maps341of348 footprints to97unique prefabs.

The preview camera uses an elevated opposite-corner view (offset -0.7, 0.3, 0.7), rotated 180 degrees horizontally from the initial previews while retaining the two-sided angle. Prefab orientations vary, so this is not automatic entrance detection.

Preview generation uses actual game-prefab models, an isolated temporary camera and `System.MakeScreenshot`, not AI images or invented architectural substitutes. Invoke `GC_TestBuildingExport`, then each queue index with `GC_TestBuildingPhoto(index)`, allow rendering to settle, and `GC_TestBuildingPhotoSave(index)`; allow the deferred screenshot to finish before advancing. Finish with `GC_TestBuildingPhotoEnd`. Native `SCR_EditorImagePositionEntity.DeactivatePosition` (1.8.0.13, lines408–418) informs attachment cleanup: query the capture region and delete new entities while retaining the original set and camera. The capture position is runtime-only, no world is saved. Rebuild the catalog after capturing; only image files newer than their matching resource sidecar are included. Images are resized to640pxWebP and shared by prefab type; they do not depict the exact map instance or recorded destruction state. Keep source export and original captures when regenerating.

Validation: all97model previews are packaged and341footprints resolve. All41playback Node tests passed; browser checks covered real footprint clicks, image loading, continued playback, Escape, dragging, mobile overflow, and existing pan/zoom cache behavior. Local Release build passed with0warnings/errors and serves the catalog/module. Workbench compiled; camera restoration and no-preview rejection passed. The shared production admin guard rejected an unresolved requester; a real remote logged-out session was not exercised for this offline helper. Images are bound by exact resource sidecar contents across capture sessions because entity-query row order is transient.
