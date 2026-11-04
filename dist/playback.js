import { recordingCalendar } from "./playback-calendar.mjs";
import { combatCheckpoints } from "./playback-checkpoints.mjs";
import { playbackControls } from "./playback-controls.mjs";
import { playbackPreferences } from "./playback-preferences.mjs";
import { TerrainRenderer } from "./playback-terrain-client.mjs";
import { findBuilding, buildingModels, findMapObject } from "./playback-buildings.mjs";
import {
  buildModel,
  latest,
  mapStateAt,
  actorAt,
  actorNameAt,
  shotAt,
  position,
  bounds,
  label,
  eventActors,
  eventActorAt,
  factionKey,
  factionColor,
} from "./playback-model.mjs";
import { drawMapShapes, mapColor } from "./playback-map.mjs";
import { markerIcon } from "./playback-marker-icons.mjs";
import { actorIcon } from "./playback-icons.mjs";
import { unitSymbolSize } from "./playback-unit-scale.mjs";
import { drawVehicle } from "./playback-vehicles.mjs";
import { drawVoiceGlyph } from "./playback-voice.mjs";
import {
  prepareTopography,
  contourIntervalForScale,
  prepareMapSymbols,
  drawMapSymbols,
  prepareMapLabels,
  drawMapLabels,
} from "./playback-topography.mjs";
import { recordingWindows } from "./playback-loader.mjs";
const $ = (id) => document.getElementById(id),
  canvas = $("map"),
  ctx = canvas.getContext("2d");
const preferences = playbackPreferences();
const transportControls = playbackControls(document.querySelector('.transport'), document.getElementById('play'));
let mapSymbols = [];
let mapLabels = [];
let naturalImages = {};
let topography = null;
let terrainData = null;
let recordings = [],
  nextOffset = null,
  model = null,
  summary = null,
  current = null,
  clock = 0,
  playing = false,
  selected = null,
  following = null,
  selectedEvent = null,
  enabled = new Set(),
  eventLimit = 100,
  abort = null;
let view = { x: 0, z: 0, scale: 1 },
  width = 1,
  height = 1,
  hitActors = [],
  mapImage = null,
  mapConfig = null;
let factions = new Set();
const factionColors = new Map();
const iconImages = new Map();
const mapGlyphs = new Map();
function iconImage(icon) {
  if (!icon) return null;
  if (!iconImages.has(icon.url)) {
    const image = new Image();
    image.onload = () => draw();
    image.src = icon.url;
    iconImages.set(icon.url, image);
  }
  const image = iconImages.get(icon.url);
  return image.complete && image.naturalWidth ? image : null;
}
// Cache white role artwork and its subtle edge once, not for every replay frame.
const roleGlyphs = new Map();
function roleGlyph(image) {
  if (!image) return null;
  if (!roleGlyphs.has(image.src)) {
    const mask = document.createElement("canvas");
    mask.width = mask.height = 48;
    const ink = mask.getContext("2d");
    ink.drawImage(image, 0, 0, 48, 48);
    ink.globalCompositeOperation = "source-in";
    ink.fillStyle = "#fff";
    ink.fillRect(0, 0, 48, 48);
    const glyph = document.createElement("canvas");
    glyph.width = glyph.height = 52;
    const paint = glyph.getContext("2d");
    paint.shadowColor = "rgba(16, 23, 15, .8)";
    paint.shadowBlur = 2;
    paint.drawImage(mask, 2, 2);
    roleGlyphs.set(image.src, glyph);
  }
  return roleGlyphs.get(image.src);
}
// Category symbols stay upright; the pointer carries world orientation.
function drawFacing(p, heading, color, radius) {
  if (heading === null || !checked("facing")) return;
  ctx.save();
  ctx.translate(...p);
  ctx.rotate((heading * Math.PI) / 180);
  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.moveTo(0, -radius - 12);
  ctx.lineTo(5, -radius - 2);
  ctx.lineTo(-5, -radius - 2);
  ctx.closePath();
  ctx.fill();
  ctx.restore();
}
let loadedUntil = 0;
let eventCursor = -2;
const checked = (id) => {
  if (id === "icons" || id === "interpolate") return true;
  if (id === "directions") return false;
  if (id === "terrainTrees" || id === "terrainBushes") id = "vegetation";
  if (id === "mapShapes" || id === "mapMarkers") id = "annotations";
  return $(id).checked;
};
const time = (ms) => {
  const s = Math.floor(Math.max(0, ms) / 1000);
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
};
const displayTime = (t) => time(t - (summary?.fromMs ?? 0));
async function get(url, signal) {
  const response = await fetch(url, {
    cache: "no-store",
    signal: AbortSignal.any([
      ...(signal ? [signal] : []),
      AbortSignal.timeout(15000),
    ]),
  });
  if (!response.ok)
    throw new Error(
      response.status === 401
        ? "Sign in to view playback."
        : response.status === 403
          ? "Playback requires the Discord Developer role."
          : `Could not load data (${response.status}).`,
    );
  const text = await response.text();
  if (text.length > 3 * 1024 * 1024)
    throw new Error("Response exceeded the playback limit.");
  return JSON.parse(text);
}
const calendar = recordingCalendar($("recordingCalendar"), () => list());
let recordingBusy = 0;
let recordingMaps = [];
function busyRecording() {
  recordingBusy++;
  $("recordingLoading").hidden = false;
  $("reload").classList.add("refreshing");
  $("reload").disabled = true;
  $("older").disabled = true;
  calendar.setBusy(true);
  $("status").classList.remove("recording-error");
  return () => {
    if (--recordingBusy) return;
    $("recordingLoading").hidden = true;
    $("reload").classList.remove("refreshing");
    $("reload").disabled = false;
    $("older").disabled = false;
    calendar.setBusy(false);
  };
}
function recordingTitle(r) {
  const world = (r.world || "").toLowerCase().replaceAll("\\", "/");
  const map = recordingMaps.find(m => world.endsWith(m.worldSuffix.toLowerCase().replaceAll("\\", "/")));
  return [r.missionName?.trim(), map?.name || label(r.world) || "Unknown map"].filter(Boolean).join(" · ");
}
function recordingDate(r) {
  const date = new Date(r.startedAt * 1000);
  return Number.isFinite(date.getTime()) ? date.toLocaleString(undefined, {
    year: "numeric", month: "short", day: "numeric", hour: "numeric", minute: "2-digit",
  }) : "";
}
let initialRecordingLink = true;
let appliedRecordingDay = "";
function recordingDateQuery() { return calendar.query(); }

function message(text, error = false) {
  $("status").classList.toggle("recording-error", error);
  $("status").textContent = text;
}
function pause() {
  playing = false;
  transportControls.setPlaying(false);
}
function controls(on) {
  if (!on) $("timelineCheckpoints").replaceChildren();
  for (const id of ["play", "timeline"])
    $(id).disabled = !on;
}
async function list(append = false) {
  const finished = busyRecording();
  try {
    // Optional map names must not prevent recordings from loading.
    recordingMaps = await get("/api/playback/maps").catch(() => []);
    const result = await get(
      `/api/admin/recordings?offset=${append ? nextOffset : 0}${recordingDateQuery()}`,
    );

    recordings = append
      ? [...recordings, ...result.recordings]
      : result.recordings;
    nextOffset = result.nextOffset;
    if (!append && initialRecordingLink) {

      const query = new URLSearchParams(location.search);
      const serverId = query.get("server"), recordingId = query.get("recording");
      if (serverId && recordingId) {
        let target = recordings.find(r => r.serverId === serverId && r.recordingId === recordingId);
        if (!target) {
          const detail = await get(`/api/admin/recordings/${encodeURIComponent(serverId)}/${encodeURIComponent(recordingId)}`);
          target = { serverId, recordingId, missionId: detail.metadata?.missionId,
            startedAt: detail.metadata?.startedAt, world: detail.metadata?.world };
          if (target.missionId) {
            const mission = await get(`/api/admin/missions/${encodeURIComponent(serverId)}/${encodeURIComponent(target.missionId)}`).catch(() => null);
            target.missionName = mission?.streams?.history?.metadata?.missionName;
          }
          recordings.unshift(target);
        } else recordings = [target, ...recordings.filter(r => r !== target)];
      }
    }
    if (!append && initialRecordingLink) {
      const target = recordings[0];
      if (target) {
        calendar.selectTimestamp(target.startedAt);
        const day = await get(`/api/admin/recordings?offset=0${recordingDateQuery()}`);
        recordings = day.recordings;
        const linked = new URLSearchParams(location.search).get("recording");
        if (linked === target.recordingId) recordings = [target, ...recordings.filter(r => r.recordingId !== target.recordingId || r.serverId !== target.serverId)];
        nextOffset = day.nextOffset;
      }
      initialRecordingLink = false;
    }
    appliedRecordingDay = calendar.value;
    const old = $("recording").value;
    $("recording").replaceChildren();
    for (const [i, r] of recordings.entries()) {
      const option = document.createElement("option");
      option.value = i;
      option.textContent = recordingTitle(r);
      $("recording").append(option);
    }
    $("older").hidden = nextOffset == null;
    if (append) $("recording").value = old;
    else if (recordings.length) await load(recordings[0]);
    else {
      abort?.abort(); pause(); model = null; summary = null; current = null;
      controls(false); $("recordingDate").textContent = ""; $("recording").title = "";
      $("recording").append(new Option("No recordings", ""));
      $("actors").replaceChildren(); $("events").replaceChildren(); draw();
      message("No recordings for these dates.", true);
    }
  } catch (e) {
    calendar.select(appliedRecordingDay);
    message(e.message, true);
  } finally { finished(); }
}
async function load(recording) {
  const finished = busyRecording();
  $("mapNotice").hidden = true;
  abort?.abort();
  abort = new AbortController();
  const signal = abort.signal;
  pause();
  controls(false);
  model = null;
  summary = null;
  current = recording;
  $("recording").title = recordingTitle(recording);
  $("recordingDate").textContent = recordingDate(recording);
  if (recording.missionId) {
    const sessionLink = document.createElement("a");
    sessionLink.textContent = "View session";
    sessionLink.href = "/sessions/" + encodeURIComponent(recording.serverId) + "/" + encodeURIComponent(recording.missionId);
    $("recordingDate").append(" · ", sessionLink);
  }
  preferences.loadUnits(recording.serverId + ":" + recording.recordingId);
  selected = null;
  following = null;
  selectedEvent = null;
  mapImage = null;
  mapSymbols = [];
  $("symbolCoverage").textContent = "No facility symbols for this world.";
  mapLabels = [];
  $("labelCoverage").textContent = "No place labels for this world.";
  naturalImages = {};
  $("naturalCoverage").textContent = "No scenery layers loaded.";
  topography = null;
  terrainData = null;
  closeBuilding();

  $("terrainCoverage").textContent = "Select a recording.";
  mapConfig = null;
  $("actors").replaceChildren();
  $("factions").replaceChildren();
  $("shotCoverage").textContent = "Loading shot coverage…";
  $("events").replaceChildren();
  $("statistics").textContent = "Loading…";
  $("coverage").textContent = "";
  $("eventData").textContent = "No event selected.";
  $("mapName").textContent = "";
  enabled = new Set();
  loadedUntil = 0;
  message("Loading recording details...");
  try {
    draw();
    const base = `/api/admin/recordings/${recording.serverId}/${recording.recordingId}`;
    const info = await get(base, signal);
    if (signal.aborted) return;
    if (!info.closed)
      throw new Error(
        "This recording is still open. End the session and reload to inspect a stable recording.",
      );
    if (
      !info.allBatchesPresent ||
      info.status === "pending" ||
      info.status === "incomplete"
    )
      throw new Error(
        "This recording is incomplete or still processing. Reload after intake finishes.",
      );
    if (info.recordCount > 100000 || info.durationMs > 7200000)
      throw new Error(
        "This first playback viewer supports up to 100,000 records or two hours per recording.",
      );
    summary = info;
    void loadMap(info, recording, signal);
    clock = info.fromMs;
    loadedUntil = info.fromMs;
    const impacts =
      (info.recordTypes.impact ?? 0) + (info.recordTypes.detonation ?? 0);
    $("shotCoverage").textContent = impacts
      ? `Server shot positions · ${impacts} recorded contacts / detonations`
      : "Shot positions sampled · no impacts recorded";
    $("coverage").textContent = JSON.stringify(
      {
        status: info.status,
        droppedRecords: info.droppedRecords,
        coverage: info.metadata?.coverage,
      },
      null,
      2,
    );
    $("statistics").textContent =
      `Recording event counts\n${JSON.stringify(info.recordTypes, null, 2)}`;
    $("timeline").min = info.fromMs;
    eventLimit = 100;
    for await (const part of recordingWindows(info, base, get, signal)) {
      if (signal.aborted) return;
      const first = !model;
      const previousActors = new Set(model?.actors.keys() ?? []);
      model = buildModel(part.rows);
      loadedUntil = part.until;
      for (const id of model.actors.keys())
        if (!previousActors.has(id) && preferences.unitEnabled(id)) enabled.add(id);
      renderMapFactions();
      renderFactions(!first);
      renderActors();
      renderEvents();
      $("timeline").max = loadedUntil;
      renderCheckpoints();
      if (first) fit();
      controls(true);
      update();
      message(
        `Ready to play · ${displayTime(loadedUntil)} / ${time(info.durationMs)} loaded · ${part.rows.length.toLocaleString()} / ${info.recordCount.toLocaleString()} records${part.complete ? " · checking completeness..." : " · loading more..."}`,
      );
      // Let the browser paint and accept Play while further windows arrive.
      await new Promise((resolve) => setTimeout(resolve, 0));
    }
    const verify = await get(base, signal);
    if (signal.aborted) return;
    if (
      verify.recordCount !== info.recordCount ||
      verify.validatedBatches !== info.validatedBatches ||
      verify.status !== info.status
    )
      throw new Error(
        "Recording changed during loading. Press Reload to retry.",
      );
    message(
      `${info.recordCount.toLocaleString()} records · fully loaded · ${info.status}`,
    );
    // Statistics are supplemental and must never hold playback hostage.
    void loadPlayers(recording, signal);
  } catch (e) {
    if (signal.aborted) return;
    pause();
    message(
      (e.name === "TimeoutError"
        ? "The server took too long to respond."
        : e.message) +
        (e.message.includes("Reload") ? "" : " Press Refresh to retry."), true,
    );
    $("shotCoverage").textContent = model
      ? "Partially loaded · see loading status"
      : "Recording could not be loaded";
    if (!model)
      $("statistics").textContent = "Unavailable until the recording loads.";
    controls(!!model);
  } finally { finished(); }
}
function mapNotice(world, failed = false, raster = false) {
  $("mapNotice").hidden = false;
  $("mapNoticeTitle").textContent = raster ? "Basic map only" : failed ? "Map could not load" : "Map background missing";
  $("mapNoticeText").textContent = raster
    ? "Using the game map texture. Import a full map package for terrain detail and object previews."
    : failed
    ? "Playback can continue without the background. Reload to retry, or check the map package."
    : `This replay has no map package${world ? ": " + world : "; its world was not recorded"}. Actors and events can still play on the coordinate grid.`;
  $("mapNoticeAction").textContent = failed ? "Manage maps →" : "Add map package →";
  $("mapNoticeAction").href = "/admin?world=" + encodeURIComponent(world || "") + "#maps";
}
async function loadMap(info, recording, signal) {
  try {
    // Calibrated maps are optional. Unknown worlds remain an explicitly labelled metric plot.
    const maps = await get("/api/playback/maps", signal);
    if (signal.aborted) return;
    const world = info.metadata?.world ?? recording.world ?? "";
    const config = maps.find((m) =>
      world
        .toLowerCase()
        .replaceAll("\\", "/")
        .endsWith(m.worldSuffix.toLowerCase().replaceAll("\\", "/")),
    );
    if (config) {
      mapConfig = config;
      const basic = config.quality === "raster" || !config.details;
      if (basic) mapNotice(world, false, true);
      if (config.symbols) {
        $("symbolCoverage").textContent = "Loading facility symbols…";
        get(config.symbols, signal)
          .then((data) => {
            if (signal.aborted) return;
            mapSymbols = prepareMapSymbols(data);
            $("symbolCoverage").textContent =
              `${mapSymbols.length} game map symbols`;
            draw();
          })
          .catch(() => {
            if (!signal.aborted)
              $("symbolCoverage").textContent =
                "Facility symbols unavailable. Reload to retry.";
          });
      }
      if (config.labels) {
        $("labelCoverage").textContent = "Loading place names…";
        get(config.labels, signal)
          .then((data) => {
            if (signal.aborted) return;
            mapLabels = prepareMapLabels(data);
            $("labelCoverage").textContent =
              `${mapLabels.length} named locations`;
            draw();
          })
          .catch(() => {
            if (!signal.aborted)
              $("labelCoverage").textContent =
                "Place names unavailable. Reload to retry.";
          });
      }
      let fallbackLoaded = false;
      const image = new Image();
      image.onload = () => {
        if (signal.aborted) return;
        mapConfig = config;
        mapImage = image;
        $("mapName").textContent = config.name;
        draw();
      };
      image.onerror = () => {
        if (signal.aborted) return;
        if (config.fallbackImage && image.src !== new URL(config.fallbackImage, location.href).href) {
          fallbackLoaded = true;
          image.src = config.fallbackImage;
          mapNotice(world, false, true);
          return;
        }
        mapNotice(world, true);
        $("mapName").textContent = config.name;
      };
      image.src = config.image;
      if (config.natural) {
        const entries = Object.entries(config.natural);
        let loaded = 0,
          failed = 0;
        $("naturalCoverage").textContent = "Loading scenery…";
        for (const [layer, url] of entries) {
          const layerImage = new Image();
          const report = () => {
            $("naturalCoverage").textContent = failed
              ? `${loaded}/${entries.length} scenery layers loaded; ${failed} unavailable.`
              : loaded === entries.length
                ? "Static vegetation & rocks · shaded terrain"
                : `Loading scenery… ${loaded}/${entries.length}`;
          };
          layerImage.onload = () => {
            if (signal.aborted) return;
            naturalImages[layer] = layerImage;
            if (layer === "relief")
              $("mapName").textContent = config.name;
            loaded++;
            report();
            draw();
          };
          layerImage.onerror = () => {
            if (signal.aborted) return;
            failed++;
            report();
          };
          layerImage.src = url;
        }
      }
      if (config.details) {
        $("terrainCoverage").textContent = "Loading terrain detail…";
        try {
          let data = await get(config.details, signal);
          if (Array.isArray(data.parts)) {
            if (data.parts.length > 128) throw new Error("Too many terrain parts");
            const features = [];
            // Bound concurrent downloads; each part retains the normal JSON limit.
            for (let i = 0; i < data.parts.length; i += 3) {
              const parts = await Promise.all(data.parts.slice(i, i + 3).map(url => get(url, signal)));
              for (const part of parts) features.push(...part.features);
              if (signal.aborted) return;
              $("terrainCoverage").textContent = `Loading terrain detail… ${Math.min(i + 3, data.parts.length)}/${data.parts.length}`;
            }
            data = { version: 1, bounds: config.bounds, features, source: data.source };
          }
          if (signal.aborted) return;
          topography = prepareTopography(data);
          terrainData = data;

          $("terrainCoverage").textContent =
            "Buildings, walls, roads, inland water · adaptive contours";
          draw();
        } catch {
          if (!signal.aborted)
            $("terrainCoverage").textContent =
              "Terrain detail unavailable. Reload to retry.";
        }
      } else
        $("terrainCoverage").textContent =
          "No detailed terrain for this world.";
    } else {
      mapNotice(world);
      $("mapName").textContent = "";
      $("terrainCoverage").textContent = "No detailed terrain for this world.";
    }
  } catch {
    if (!signal.aborted) {
      mapNotice(info.metadata?.world ?? recording.world, true);
      $("mapName").textContent = "";
    }
  }
}

async function loadPlayers(recording, signal) {
  try {
    const players = [];
    let after = null;
    do {
      const page = await get(
        `/api/admin/missions/${recording.serverId}/${recording.missionId}/players${after ? "?after=" + encodeURIComponent(after) : ""}`,
        signal,
      );
      if (signal.aborted) return;
      players.push(...page.players);
      if (players.length > 10000 || (after && page.nextAfter === after))
        throw new Error("Invalid player pagination.");
      after = page.nextAfter;
    } while (after);
    model = buildModel(model.rows, players);
    renderCheckpoints();
    $("statistics").textContent +=
      `\n\nMission totals (latest, not seek time)\n${players.map((p) => `${p.name || p.identity}\n${JSON.stringify(p.counters, null, 2)}`).join("\n\n") || "No player statistics received."}`;
    renderActors();
    renderEvents();
    update();
  } catch (e) {
    if (!signal.aborted)
      $("statistics").textContent +=
        "\n\nPlayer statistics could not be loaded. Playback is still available.";
  }
}

function renderMapFactions() {
  const selected = preferences.markerFaction();
  $("mapFaction").replaceChildren();
  for (const key of [...model.mapStates.keys()].sort()) {
    const option = document.createElement("option");
    option.value = key;
    option.textContent = key || "Everyone";
    $("mapFaction").append(option);
  }
  $("mapFaction").value = model.mapStates.has(selected) ? selected : "";
  $("mapFaction").disabled = !model.mapStates.size;
}
function drawRecordedMap() {
  const state = mapStateAt(model, $("mapFaction").value, clock);
  $("mapCoverage").textContent = !model.mapStates.size
    ? "Map layers were not recorded."
    : !state
      ? "Map layers have not arrived at this time."
      : `${state.markers.length} markers · ${state.shapes.length} shapes${state.markers.some((m) => !markerIcon(m) && !actorIcon(m)) ? " · some symbols shown generically" : ""}${!state.markersAvailable ? " · shared markers unavailable" : ""}${!state.drawingsAvailable ? " · drawing integration unavailable" : ""}`;
  if (!state) return;
  if (checked("mapShapes"))
    drawMapShapes(ctx, state.shapes, screen, view.scale, width, height);
  if (!checked("mapMarkers")) return;
  for (const marker of state.markers) {
    const p = screen(marker.position);
    const size = Math.min(
      2048,
      Math.max(
        marker.minSize,
        marker.size * (marker.worldScale ? view.scale : 1),
      ),
    );
    const icon = markerIcon(marker) ?? actorIcon(marker);
    const image = iconImage(icon);
    ctx.save();
    ctx.translate(...p);
    ctx.rotate((marker.rotation * Math.PI) / 180);
    ctx.strokeStyle = mapColor(marker.color);
    ctx.fillStyle = mapColor(marker.color);
    if (image) {
      // Tint only the icon's alpha; never paint over the existing map canvas.
      const key = icon.url + ":" + marker.color;
      let glyph = mapGlyphs.get(key);
      if (!glyph) {
        glyph = document.createElement("canvas");
        glyph.width = image.naturalWidth;
        glyph.height = image.naturalHeight;
        const paint = glyph.getContext("2d");
        paint.drawImage(image, 0, 0);
        paint.globalCompositeOperation = "source-in";
        paint.fillStyle = mapColor(marker.color);
        paint.fillRect(0, 0, glyph.width, glyph.height);
        if (mapGlyphs.size >= 1024)
          mapGlyphs.delete(mapGlyphs.keys().next().value);
        mapGlyphs.set(key, glyph);
      }
      ctx.drawImage(
        glyph,
        -size / 2,
        -size / 2,
        size,
        (size * image.naturalHeight) / image.naturalWidth,
      );
    } else {
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.moveTo(0, -8);
      ctx.lineTo(8, 0);
      ctx.lineTo(0, 8);
      ctx.lineTo(-8, 0);
      ctx.closePath();
      ctx.stroke();
    }
    ctx.restore();
    if (marker.label) {
      ctx.font = "13px system-ui";
      ctx.textAlign = "center";
      ctx.lineWidth = 3;
      ctx.strokeStyle = "#182016";
      ctx.fillStyle = mapColor(marker.color);
      ctx.strokeText(marker.label, p[0], p[1] + size / 2 + 15);
      ctx.fillText(marker.label, p[0], p[1] + size / 2 + 15);
      ctx.textAlign = "start";
    }
  }
}

function visible(actor) {
  return (
    enabled.has(actor.id) &&
    factions.has(actor.faction) &&
    checked(
      actor.kind === "player"
        ? "players"
        : actor.kind === "vehicle"
          ? "vehicles"
          : "ai",
    ) &&
    (!actor.dead || checked("dead"))
  );
}
function colorFor(key) {
  return factionColors.get(key) ?? preferences.factionColor(key) ?? factionColor(key);
}
function renderFactions(preserve = false) {
  const previous = factions;
  const known = new Set(
    [...$("factions").querySelectorAll("input[type=checkbox]")].map(
      (input) => input.value,
    ),
  );
  const available = new Set(
    [...model.actors.values()].flatMap((a) =>
      a.states.map((s) => factionKey(s.data)),
    ),
  );
  factions = new Set(
    [...available].filter(
      (key) => preserve && known.has(key) ? previous.has(key) : preferences.factionEnabled(key),
    ),
  );
  $("factions").replaceChildren();
  for (const key of [...available].sort()) {
    const row = document.createElement("div");
    row.className = "faction-row";
    const label = document.createElement("label");
    const check = document.createElement("input");
    check.type = "checkbox";
    check.value = key;
    check.checked = factions.has(key);
    check.onchange = () => {
      check.checked ? factions.add(key) : factions.delete(key);
      preferences.setFaction(key, check.checked);
      renderEvents();
      update();
    };
    label.append(check, document.createTextNode(` ${key}`));
    const color = document.createElement("input");
    color.type = "color";
    color.value = colorFor(key);
    color.setAttribute("aria-label", `${key} faction color`);
    color.oninput = () => {
      factionColors.set(key, color.value);
      preferences.setColor(key, color.value);
      renderCheckpoints();
      update();
    };
    row.append(label, color);
    $("factions").append(row);
  }
}
function renderActors() {
  const search = $("search").value.toLowerCase();
  $("actors").replaceChildren();
  if (!model) return;
  for (const actor of model.actors.values()) {
    const name = actorNameAt(model, actor, clock);
    if (!`${name} ${actor.id}`.toLowerCase().includes(search)) continue;
    const row = document.createElement("div");
    row.className = "actor-row";
    const input = document.createElement("input");
    input.type = "checkbox";
    input.checked = enabled.has(actor.id);
    input.setAttribute("aria-label", `Show ${name}`);
    input.onchange = () => {
      input.checked ? enabled.add(actor.id) : enabled.delete(actor.id);
      preferences.setUnit(actor.id, input.checked);
      renderEvents();
      draw();
    };
    const button = document.createElement("button");
    const title = document.createElement("span");
    title.className = "actor-name";
    title.dataset.actor = actor.id;
    title.textContent = name;
    button.append(title);
    const badge = document.createElement("small");
    badge.className = "actor-faction";
    badge.dataset.actor = actor.id;
    const badgeState = actorAt(model, actor, clock, false);
    badge.textContent = badgeState ? badgeState.faction : "Not present";
    badge.style.color = badgeState ? colorFor(badgeState.faction) : "";
    button.append(badge);
    button.setAttribute("aria-pressed", String(selected === actor.id));
    button.onclick = () => selectEntity(actor.id);
    row.append(input, button);
    $("actors").append(row);
  }
}
function eventName(row) {
  const d = row.data,
    name = (id) => {
      const actor = model.actors.get(id);
      return actor ? actorNameAt(model, actor, row.timeMs) : id || "unknown";
    };
  if (row.type === "shot" || row.type === "grenade")
    return `${name(d.entity)} · ${row.type}`;
  if (row.type === "damage")
    return `${name(d.attacker)} → ${name(d.entity)} · ${Number(d.reportedDamage ?? 0).toFixed(1)} reported damage`;
  if (row.type === "death")
    return d.killer
      ? `${name(d.killer)} killed ${name(d.entity)}`
      : `${name(d.entity)} died`;
  return row.type;
}
function renderEvents() {
  if (!model) return;
  const categories = new Set(
    [...document.querySelectorAll("[data-event]:checked")].map(
      (x) => x.dataset.event,
    ),
  );
  eventCursor = latest(model.events, clock);
  const events = model.events.slice(0, eventCursor + 1).filter((r) => {
    if (!["shot", "grenade", "damage", "death"].includes(r.type)) return false;
    const category = ["shot", "grenade"].includes(r.type) ? "shots" : "combat";
    return (
      categories.has(category) &&
      (r.type === "gap" ||
        eventActors(r, model).some((id) => {
          const a = model.actors.get(id);
          const state = a && eventActorAt(model, a, r);
          return state && visible(state);
        }))
    );
  });
  events.reverse();
  $("events").replaceChildren();
  for (const row of events.slice(0, eventLimit)) {
    const button = document.createElement("button");
    button.className = "event";
    button.dataset.time = row.timeMs;
    button.dataset.sequence = row.sequence;
    const stamp = document.createElement("time");
    stamp.textContent = displayTime(row.timeMs);
    button.append(stamp, document.createTextNode(eventName(row)));
    button.onclick = () => {
      following = null;
      selectedEvent = row.sequence;
      pause();
      seek(row.timeMs);
      const actor = model.actors.get(row.data.entity);
      // Damage hitPosition is sometimes unset (0,0,0); focus its recorded actor.
      const p =
        row.type === "damage"
          ? actor && actorAt(model, actor, clock, false)?.position
          : (position(row.data) ?? row.data.origin);
      if (p) {
        view.x = p[0];
        view.z = p[2];
      }
      selected = row.data.entity ?? null;
      $("eventData").textContent = JSON.stringify(row, null, 2);
      renderActors();
      update();
    };
    $("events").append(button);
  }
  if (!events.length)
    $("events").textContent =
      "No activity yet at this time with these filters.";
  $("moreEvents").hidden = events.length <= eventLimit;
  updateEvents();
}
function updateEvents() {
  if (model && latest(model.events, clock) !== eventCursor) {
    renderEvents();
    return;
  }
  for (const button of $("events").querySelectorAll("button")) {
    button.classList.toggle("future", Number(button.dataset.time) > clock);
    button.classList.toggle(
      "active",
      Number(button.dataset.sequence) === selectedEvent,
    );
  }
}
function renderCheckpoints() {
  const container = $("timelineCheckpoints");
  container.replaceChildren();
  if (!model || !summary || !checked("timelineCombat")) return;
  const duration = loadedUntil - summary.fromMs;
  if (duration <= 0) return;
  const fragment = document.createDocumentFragment();
  for (const point of combatCheckpoints(model)) {
    if (point.timeMs < summary.fromMs || point.timeMs > loadedUntil) continue;
    const tick = document.createElement("button");
    tick.type = "button";
    tick.className = "timeline-checkpoint " + point.kind;
    tick.style.left = ((point.timeMs-summary.fromMs)/duration*100) + "%";
    tick.style.color = colorFor(point.faction);
    tick.title = `${displayTime(point.timeMs)} | ${point.name} | ${point.kind === "kill" ? "Kill" : "Death"} | ${point.faction}`;
    tick.setAttribute("aria-label",tick.title);
    tick.onclick = () => seek(point.timeMs);
    fragment.append(tick);
  }
  container.append(fragment);
}
$("timelineCombat").onchange = renderCheckpoints;
function selectEntity(id) {
  selected = following = id;
  closeBuilding();
  renderActors();
  update();
}
function clearEntitySelection() {
  if (selected === null && following === null) return;
  selected = following = null;
  renderActors();
  update();
}
function seek(t) {
  if (!summary) return;
  clock = Math.max(summary.fromMs, Math.min(loadedUntil, t));
  update();
}
function update() {
  if (!model) return;
  if ($("search").value) renderActors();
  for (const title of $("actors").querySelectorAll(".actor-name")) {
    const name = actorNameAt(
      model,
      model.actors.get(title.dataset.actor),
      clock,
    );
    if (title.textContent !== name) {
      renderActors();
      break;
    }
  }
  for (const badge of $("actors").querySelectorAll(".actor-faction")) {
    const actor = model.actors.get(badge.dataset.actor),
      state = actorAt(model, actor, clock, false);
    badge.textContent = state ? state.faction : "Not present";
    badge.style.color = state ? colorFor(state.faction) : "";
  }
  $("timeline").value = clock;
  $("clock").textContent =
    `${displayTime(clock)} / ${time(summary.durationMs)}`;
  updateEvents();
  draw();
  const actor = model.actors.get(selected);
  const state = actor && actorAt(model, actor, clock, checked("interpolate"));
  $("selection").hidden = !actor;
  if (!state) {
    $("selection").textContent = actor
      ? "This actor is not present at this time."
      : "Select an actor to inspect stance, equipment and vehicle.";
    return;
  }
  const d = state.data,
    vehicle = model.actors.get(d.vehicle);
  $("selection").textContent = [
    state.name + (state.dead ? " · Dead" : ""),
    [d.stance != null ? (["Standing", "Crouched", "Prone"][d.stance] ?? "") : "",
      d.vehicle ? (vehicle?.name ?? "In vehicle") : "",
      d.weaponPrefab ? label(d.weaponPrefab) : ""].filter(Boolean).join(" · "),
    state.gap ? "Recording gap" : "",
  ].filter(Boolean).join("\n");
}
function screen(p) {
  return [
    (p[0] - view.x) * view.scale + width / 2,
    height / 2 - (p[2] - view.z) * view.scale,
  ];
}
function world(x, y) {
  return [
    view.x + (x - width / 2) / view.scale,
    0,
    view.z - (y - height / 2) / view.scale,
  ];
}
function fit(extent) {
  if (!model) return;
  clearEntitySelection();
  // A cached recording may finish before the first ResizeObserver callback.
  width = canvas.clientWidth;
  height = canvas.clientHeight;
  if (!width || !height) return;
  const b = extent ?? bounds(model);
  view = {
    x: (b[0] + b[2]) / 2,
    z: (b[1] + b[3]) / 2,
    scale: Math.min(
      (width - 80) / (b[2] - b[0]),
      (height - 170) / (b[3] - b[1]),
    ),
  };
  view.scale = Math.max(0.01, Math.min(20, view.scale));
  draw();
}
let drawRequested = false;
let zoomUntil = 0, zoomPending = false;
function draw() { drawRequested = true; }
const terrainRenderer = new TerrainRenderer(() => draw(), error => {
  $("terrainCoverage").textContent = "Terrain rendering unavailable. Reload to retry.";
  console.error("Terrain renderer:", error);
}, canvas);
function drawNow() {
  const target = model?.actors.get(following);
  const tracked = target && actorAt(model, target, clock, checked("interpolate"));
  if (tracked?.position && visible(tracked)) {
    view.x = tracked.position[0];
    view.z = tracked.position[2];
  }
  ctx.clearRect(0, 0, width, height);
  hitActors = [];
  const dpr = canvas.width / width;
  const options = Object.fromEntries(["terrainRelief", "terrainWater", "terrainTrees", "terrainBushes",
    "terrainRocks", "terrainRoads", "terrainBuildings", "terrainContours", "terrainPower"].map(k => [k, checked(k)]));
  options.sceneryOpacity = Number($("sceneryOpacity").value);
  const interval = contourIntervalForScale(view.scale);
  const contourKey = !options.terrainContours ? "Contours hidden." : options.terrainRelief && view.scale <= .45
    ? "Contours appear as you zoom in. Elevations in metres."
    : `${interval} m intervals · index every ${interval * 5} m${options.terrainRelief ? " · fades with zoom" : ""}.`;
  if ($("contourKey").textContent !== contourKey) $("contourKey").textContent = contourKey;
  terrainRenderer.update({mapConfig, mapImage, naturalImages, terrainData, topography, options}, view, width, height, dpr,
    performance.now() < zoomUntil);
  try {terrainRenderer.paint(ctx, view, width, height);}
  catch(error) {terrainRenderer.compositorError=error;terrainRenderer.fail(error);}
  if (selectedBuilding?.points && checked("terrainBuildings")) {
    ctx.save();
    ctx.beginPath();
    const points = selectedBuilding.points;
    for (let i=0; i<points.length; i+=2) {
      const p = screen([points[i],0,points[i+1]]);
      if (i) ctx.lineTo(...p); else ctx.moveTo(...p);
    }
    ctx.closePath(); ctx.strokeStyle = "#fff"; ctx.lineWidth = 2; ctx.stroke();
    ctx.restore();
  }
  const spacing = Math.pow(10, Math.ceil(Math.log10(65 / view.scale))),
    left = world(0, height),
    right = world(width, 0);
  ctx.strokeStyle = "#afc7a02b";
  ctx.lineWidth = 1;
  ctx.font = "10px system-ui";
  ctx.fillStyle = "#b0bbaa";
  for (
    let x = Math.ceil(left[0] / spacing) * spacing;
    x < right[0];
    x += spacing
  ) {
    const p = screen([x, 0, 0]);
    ctx.beginPath();
    ctx.moveTo(p[0], 0);
    ctx.lineTo(p[0], height);
    ctx.stroke();
    ctx.fillText(x.toFixed(0), p[0] + 3, height - 5);
  }
  for (
    let z = Math.ceil(left[2] / spacing) * spacing;
    z < right[2];
    z += spacing
  ) {
    const p = screen([0, 0, z]);
    ctx.beginPath();
    ctx.moveTo(0, p[1]);
    ctx.lineTo(width, p[1]);
    ctx.stroke();
    ctx.fillText(z.toFixed(0), 3, p[1] - 4);
  }
  if (checked("terrainSymbols"))
    drawMapSymbols(ctx, mapSymbols, view, width, height, iconImage);
  if (checked("terrainLabels"))
    drawMapLabels(ctx, mapLabels, view, width, height);
  if (!model) return;
  drawRecordedMap();
  for (const actor of model.actors.values()) {
    const state = actorAt(model, actor, clock, checked("interpolate"));
    if (!state?.position || !visible(state)) continue;
    const color = colorFor(state.faction);
    if (checked("trails")) {
      ctx.beginPath();
      let previous = null;
      for (const row of actor.positions) {
        if (row.timeMs > clock) break;
        if (row.timeMs < clock - 3000) continue;
        const p = screen(position(row.data));
        const gap = model.gaps.some(
          (g) =>
            previous &&
            g.data.fromMs <= row.timeMs &&
            g.data.untilMs >= previous.timeMs,
        );
        if (
          !previous ||
          row.data.discontinuity ||
          row.timeMs - previous.timeMs > 5000 ||
          gap
        )
          ctx.moveTo(...p);
        else ctx.lineTo(...p);
        previous = row;
      }
      ctx.strokeStyle = color;
      ctx.globalAlpha = 0.5;
      ctx.lineWidth = 2;
      ctx.stroke();
      ctx.globalAlpha = 1;
    }
    const p = screen(state.position);
    const showIcon = checked("showActorIcons");
    let labelBounds = null;
    const icon = checked("icons") ? actorIcon(state.data) : null;
    const image = iconImage(icon);
    const baseRadius = state.kind === "vehicle" ? 19 : image || state.voice ? 14 : 7;
    const sizing = unitSymbolSize(state.kind, icon, view.scale, checked("worldScaleUnits"), baseRadius);
    const radius = sizing.radius;
    if (showIcon) {
      ctx.save();
      ctx.translate(...p);ctx.scale(sizing.factor,sizing.factor);ctx.translate(-p[0],-p[1]);
      if (state.kind !== "vehicle") drawFacing(p, state.heading, color, baseRadius);
      ctx.fillStyle = color;
      ctx.strokeStyle = "#10170f";
      ctx.lineWidth = 2;
      ctx.beginPath();
      if (state.kind === "vehicle") {
        drawVehicle(ctx, p, state.heading, color, icon);
      } else if (sizing.simple || (checked("worldScaleUnits") && !image && !state.voice)) {
        ctx.arc(...p, baseRadius, 0, Math.PI * 2);
        ctx.fill();
      } else if (state.voice) {
        ctx.arc(...p, baseRadius, 0, Math.PI * 2);
        ctx.fill();
        drawVoiceGlyph(ctx, p, state.voice);
      } else if (image) {
        ctx.arc(...p, baseRadius, 0, Math.PI * 2);
        ctx.fill();
        ctx.drawImage(roleGlyph(image), p[0] - 13, p[1] - 13, 26, 26);
      } else {
        if (state.kind === "ai") {
          ctx.moveTo(p[0], p[1] - 6);
          ctx.lineTo(p[0] + 6, p[1] + 5);
          ctx.lineTo(p[0] - 6, p[1] + 5);
          ctx.closePath();
        } else ctx.arc(...p, 5, 0, Math.PI * 2);
        ctx.fill();
      }
      if (state.dead) {
        ctx.strokeStyle = "#fff";
        ctx.beginPath();
        ctx.moveTo(p[0] - 5, p[1] - 5);
        ctx.lineTo(p[0] + 5, p[1] + 5);
        ctx.moveTo(p[0] + 5, p[1] - 5);
        ctx.lineTo(p[0] - 5, p[1] + 5);
        ctx.stroke();
      }
      ctx.restore();
      if (selected === actor.id) {
        ctx.lineWidth = 1.5;
        ctx.strokeStyle = "#fff";
        ctx.beginPath();
        ctx.arc(...p, radius + 4, 0, Math.PI * 2);
        ctx.stroke();
      }
    }
    if (checked("labels")) {
      ctx.save();
      ctx.textAlign = "left";
      ctx.textBaseline = "middle";
      const labelX = p[0] + (showIcon ? radius + 5 : 0);
      ctx.font = "12px system-ui";
      ctx.lineWidth = 4;
      ctx.strokeStyle = "#182016";
      ctx.strokeText(state.name, labelX, p[1]);
      ctx.fillStyle = color;
      ctx.fillText(state.name, labelX, p[1]);
      labelBounds = [
        labelX - 2,
        p[1] - 9,
        labelX + ctx.measureText(state.name).width + 2,
        p[1] + 9,
      ];
      ctx.restore();
    }
    if (showIcon || labelBounds)
      hitActors.push({ id: actor.id, p, showIcon, labelBounds, hitRadius: Math.max(6, radius + 3) });
  }
  if (checked("shots"))
    for (const shot of model.shots.values()) {
      const ray = shotAt(shot, clock),
        actor = ray && model.actors.get(ray.actor),
        state = actor && eventActorAt(model, actor, shot.shot);
      if (!ray || !state || !visible(state) || !ray.from || !ray.to) continue;
      const from = screen(ray.from),
        to = screen(ray.to),
        direction = ray.evidence.includes("direction");
      ctx.fillStyle = "#ffdd74";
      ctx.beginPath();
      ctx.arc(...from, 3, 0, Math.PI * 2);
      ctx.fill();
      if (direction && !checked("directions")) continue;
      ctx.strokeStyle = direction ? "#ffdd74" : "#ff9b62";
      ctx.lineWidth = 2;
      ctx.setLineDash(direction ? [5, 5] : []);
      ctx.beginPath();
      ctx.moveTo(...from);
      ctx.lineTo(...to);
      ctx.stroke();
      ctx.setLineDash([]);
      if (ray.confirmed) {
        ctx.fillStyle = "#ff624e";
        ctx.beginPath();
        ctx.arc(...to, 4, 0, Math.PI * 2);
        ctx.fill();
      }
    }
}
new ResizeObserver(() => {
  const rect = canvas.getBoundingClientRect(),
    dpr = devicePixelRatio || 1;
  width = rect.width;
  height = rect.height;
  canvas.width = Math.round(width * dpr);
  canvas.height = Math.round(height * dpr);
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  draw();
}).observe(canvas);
let pointer = null;
canvas.onpointerdown = (e) => {
  pointer = {
    x: e.offsetX,
    y: e.offsetY,
    startX: e.offsetX,
    startY: e.offsetY,
    moved: false,
  };
  canvas.setPointerCapture(e.pointerId);
};
canvas.onpointermove = (e) => {
  if (!pointer) return;
  pointer.moved ||= Math.hypot(e.offsetX-pointer.startX, e.offsetY-pointer.startY) >= 5;
  if (pointer.moved) clearEntitySelection();
  view.x -= (e.offsetX - pointer.x) / view.scale;
  view.z += (e.offsetY - pointer.y) / view.scale;
  pointer.x = e.offsetX;
  pointer.y = e.offsetY;
  draw();
};
let buildingRequest = 0;
let selectedBuilding = null;
const buildingCatalogs = new Map();
function closeBuilding() {
  buildingRequest++;
  selectedBuilding = null;
  draw();
  $("buildingInspector").hidden = true;
  $("buildingImage").hidden = true;
  $("buildingImage").removeAttribute("src");
}
async function inspectBuilding(feature) {
  closeBuilding();
  if (!feature) return;
  selectedBuilding = feature;
  draw();
  const request = buildingRequest, url = mapConfig?.buildings;
  $("buildingInspector").hidden = false;
  $("buildingTitle").textContent = "Map object";
  $("buildingStatus").textContent = "Loading model preview…";
  try {
    if (!url) throw new Error("No catalog");
    if (!buildingCatalogs.has(url)) {
      const pending = get(url).catch(error => { buildingCatalogs.delete(url); throw error; });
      buildingCatalogs.set(url, pending);
    }
    const catalog = await buildingCatalogs.get(url);
    if (request !== buildingRequest) return;
    const models = buildingModels(catalog, feature);
    if (!models.length) throw new Error("No verified match");
    if (models.length !== 1) {
      $("buildingStatus").textContent = "Combined preview needs a fresh map import.";
      return;
    }
    const showModel = (model) => {
    $("buildingTitle").textContent = model.name;
    const image = $("buildingImage");
    image.hidden = true;
    $("buildingStatus").textContent = model.image ? "Loading model preview…" : "No physical model preview available.";
    if (!model.image) { image.removeAttribute("src"); return; }
    image.alt = model.name + " — game model preview";
    image.onload = () => {
      if (request !== buildingRequest) return;
      image.hidden = false;
      $("buildingStatus").textContent = "";
    };
    image.onerror = () => {
      if (request !== buildingRequest) return;
      image.hidden = true;
      $("buildingStatus").textContent = "Preview unavailable for this object.";
    };
    image.src = model.image;
    };
    showModel(models[0]);
  } catch {
    if (request === buildingRequest)
      $("buildingStatus").textContent = "Preview unavailable for this object.";
  }
}
$("closeBuilding").onclick = () => { closeBuilding(); canvas.focus({preventScroll:true}); };
document.addEventListener("keydown", e => {
  if (e.key === "Escape" && !$("buildingInspector").hidden) {
    closeBuilding(); canvas.focus({preventScroll:true});
  }
});
canvas.onpointerup = (e) => {
  if (
    pointer && !pointer.moved &&
    Math.hypot(e.offsetX - pointer.startX, e.offsetY - pointer.startY) < 5
  ) {
    const hit = hitActors
      .filter(
        (a) =>
          (a.showIcon &&
            Math.hypot(a.p[0] - e.offsetX, a.p[1] - e.offsetY) < a.hitRadius) ||
          (a.labelBounds &&
            e.offsetX >= a.labelBounds[0] &&
            e.offsetX <= a.labelBounds[2] &&
            e.offsetY >= a.labelBounds[1] &&
            e.offsetY <= a.labelBounds[3]),
      )
      .sort(
        (a, b) =>
          Math.hypot(a.p[0] - e.offsetX, a.p[1] - e.offsetY) -
          Math.hypot(b.p[0] - e.offsetX, b.p[1] - e.offsetY),
      )[0];
    if (hit) {
      selectEntity(hit.id);
    } else {
      clearEntitySelection();
      const p = world(e.offsetX, e.offsetY);
      const symbol = checked("terrainSymbols") ? findMapObject(mapSymbols,p[0],p[2],view.scale) : null;
      inspectBuilding(symbol ?? (checked("terrainBuildings") ? findBuilding(topography, p[0], p[2], view.scale) : null));
    }
  }
  pointer = null;
};
canvas.onpointercancel = () => (pointer = null);
function zoom(factor, x = width / 2, y = height / 2) {
  // Reuse nearby terrain detail during wheel bursts; restore exact detail on settle.
  zoomUntil = performance.now() + 120;
  zoomPending = true;
  const before = world(x, y);
  view.scale = Math.max(0.01, Math.min(30, view.scale * factor));
  const after = world(x, y);
  view.x += before[0] - after[0];
  view.z += before[2] - after[2];
  draw();
}
canvas.addEventListener(
  "wheel",
  (e) => {
    e.preventDefault();
    zoom(e.deltaY < 0 ? 1.15 : 1 / 1.15, e.offsetX, e.offsetY);
  },
  { passive: false },
);
$("zoomIn").onclick = () => zoom(1.5);
$("zoomOut").onclick = () => zoom(1 / 1.5);
$("fit").onclick = () => fit();
$("whole").onclick = () => fit(mapConfig?.bounds);
function togglePlayback() {
  if ($("play").disabled || !summary) return;
  if (clock >= summary.untilMs) seek(summary.fromMs);
  playing = !playing;
  transportControls.setPlaying(playing);
}
$("play").onclick = togglePlayback;
document.addEventListener("keydown", (event) => {
  if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.isComposing) return;
  const target = event.target;
  // Keep typing and native form controls usable. The timeline itself uses the
  // same five-second shortcuts as the map, without changing playback state.
  if (target instanceof Element && (
    target.closest('[contenteditable]:not([contenteditable="false"]), textarea, select') ||
    (target.closest('input') && target.id !== "timeline") ||
    (event.code === "Space" && target.closest('button, a, summary') && target.id !== "play")
  )) return;
  if ($("play").disabled || !summary) return;
  if (event.code === "Space") {
    event.preventDefault();
    if (!event.repeat) togglePlayback();
  } else if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
    event.preventDefault();
    seek(clock + (event.key === "ArrowRight" ? 5000 : -5000));
  }
});
$("timeline").oninput = (e) => {
  seek(Number(e.target.value));
};
$("recording").onchange = (e) => load(recordings[Number(e.target.value)]);
$("reload").onclick = () => (current ? load(current) : list());
$("older").onclick = () => list(true);

$("search").oninput = renderActors;
$("mapFaction").onchange = update;
$("all").onclick = () => {
  preferences.allUnits(true);
  if (model) enabled = new Set(model.actors.keys());
  renderActors();
  renderEvents();
  draw();
};
$("none").onclick = () => {
  preferences.allUnits(false);
  enabled.clear();
  renderActors();
  renderEvents();
  draw();
};
$("resetFilters").onclick = () => {
  preferences.reset();
  factionColors.clear();
  renderCheckpoints();
  if (model) {
    enabled = new Set(model.actors.keys());
    renderFactions();
    renderMapFactions();
    renderActors();
    renderEvents();
    update();
  }
};
$("moreEvents").onclick = () => {
  eventLimit += 100;
  renderEvents();
};
document
  .querySelectorAll("#players,#ai,#vehicles,.event-filters input,#dead")
  .forEach(
    (input) =>
      (input.onchange = () => {
        renderEvents();
        update();
      }),
  );
for (const id of [
  "annotations",
  "vegetation",
  "labels",
  "trails",
  "shots",
  "facing",
  "showActorIcons",
  "worldScaleUnits",
  "terrainSymbols",
  "terrainLabels",
  "terrainRelief",
  "terrainRocks",
  "sceneryOpacity",
  "terrainRoads",
  "terrainWater",
  "terrainBuildings",
  "terrainContours",
  "terrainPower",
])
  $(id).onchange = update;
$("sceneryOpacity").oninput = draw;
document.addEventListener("visibilitychange", () => {
  if (document.hidden) pause();
});
let previous = performance.now();
function tick(now) {
  transportControls.tick(now);
  const delta = Math.min(now - previous, 250);
  previous = now;
  if (playing && summary) {
    seek(clock + delta * Number($("speed").value));
    if (clock >= summary.untilMs) pause();
    else if (clock >= loadedUntil)
      message("Buffering · waiting for the next segment...");
  }
  if (zoomPending && now >= zoomUntil) {
    zoomPending = false;
    drawRequested = true;
  }
  if (drawRequested) {
    drawRequested = false;
    drawNow();
  }
  requestAnimationFrame(tick);
}
requestAnimationFrame(tick);
list();
