# Calibrated base maps and test recordings

Everon (internal name Eden) and Kolguyev (Cain) now have real, closed Lobby2
test recordings in the local website database. These are controlled Workbench
movement/stance/AI captures, not multiplayer sessions or shooting tests.

| Terrain | Recording | Records | Actors |
| --- | --- | ---: | ---: |
| Everon | `01a19ee9-42e6-869c-8400-00000099eb26` | 945 | 9 |
| Kolguyev | `01a19eec-1abf-89dd-8400-0000009dff46` | 244 | 8 |

Both are complete, with zero dropped records and standing/crouching/prone
states. The production four-stream delivery and projection implementations
imported private copies; all 138 original payload/marker files were unchanged.
The website database role read back every record. Browser checks loaded both
calibrated maps, actor models and advancing playback without JavaScript errors.
Neither capture contains shots, kills or vehicle movement.

## Scope

These two maps now also have full Workbench-derived packages with ground colors,
water depths, vector details, scenery and building previews. Use the
[manual export and admin upload workflow](manual-map-import.md) for current map builds;
the base-raster procedure below is retained as provenance for the original test.
Map matching is by exact
verified world suffix; unrelated missions on the same terrain are not guessed.
The same raster can be registered for another verified scenario independently.

The Kolguyev test world lives in Lobby2's optional MCP test addon under
`worlds/playback/Cain_Playback.ent`. It inherits the native Cain world, with a
Lobby2 setup based on the existing Eden example and Cain's native AI world.
The player was moved to terrain height at X/Z6400/6400 before recording. The
recording also includes pre-existing scenario AI. The existing Eden example
uses the Arland AI prefab and reports a navmesh error; this capture proves
player input and recorder discovery, not AI pathfinding on Everon.

## Repeatable base-map import

1. Find the world's native MapEntity prefab through Reforger MCP. Follow its
   satellite-image resource and extract the original EDDS through `pak_extract`.
2. In a loaded Lobby2 world, explicitly call `GC_TestPlaybackMapInfo` from the
   optional test addon. It reads `SCR_MapEntity.Size()` and `Offset()`; no data is
   inferred from image pixels. Record the world, game version and operation ID.
3. Install the pinned Node dependency with `npm install --prefix scripts` and
   Python dependencies from `scripts/map-requirements.txt`.
4. Decode without changing the source:
   `node scripts/decode-map-raster.mjs original.edds decoded.dds`.
5. Register the verified north-up X/Z extent:

```text
python scripts/import-playback-map.py decoded.dds original.edds --name "Terrain name" --slug terrain-name --world worlds/example/scenario.ent --bounds MIN_X MIN_Z MAX_X MAX_Z --evidence "Game version, source resource and live calibration operation"
```

The script writes a WebP and updates `dist/playback-maps.json`, retaining source
hashes and preserving any independently generated detail layers on reimport.
The world suffix must identify the actual recording world. A custom terrain
needs its own installed raster and verified bounds; this script does not invent
missing artwork or provide a full custom-terrain export.

Both new maps were verified at X/Z[0,0]–[12800,12800], despite their raster
resolution being4096×4096. Live operations:
Everon `d0d6846143d64b0b8a87bab9e33206f4`,
Kolguyev `593022b8f04d4510a161d809f2f7e556`.
Source hashes and game version are retained in the map registry. Original
archive exports and private capture files remain outside public website files.
