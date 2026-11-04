import test from "node:test";
import assert from "node:assert/strict";
import { buildModel, mapStateAt } from "../dist/playback-model.mjs";
import { mapColor, drawMapShapes } from "../dist/playback-map.mjs";
import { markerIcon } from "../dist/playback-marker-icons.mjs";
const snapshot = (sequence, timeMs, faction, markers, shapes = []) => ({
  sequence,
  timeMs,
  type: "map-state",
  data: {
    faction,
    markers,
    shapes,
    markersAvailable: true,
    drawingsAvailable: true,
  },
});
test("map perspectives keep faction state isolated and rewind deletions", () => {
  const model = buildModel([
    snapshot(1, 0, "", [{ label: "Public" }]),
    snapshot(2, 1, "US", [{ label: "US plan" }]),
    snapshot(3, 2, "USSR", [{ label: "USSR plan" }]),
    snapshot(4, 1000, "US", []),
  ]);
  assert.equal(mapStateAt(model, "US", 0), null);
  assert.equal(mapStateAt(model, "US", 500).markers[0].label, "US plan");
  assert.equal(mapStateAt(model, "USSR", 1500).markers[0].label, "USSR plan");
  assert.equal(mapStateAt(model, "", 1500).markers[0].label, "Public");
  assert.equal(mapStateAt(model, "US", 1500).markers.length, 0);
  assert.equal(mapStateAt(model, "US", 500).markers.length, 1);
  assert.equal(mapStateAt(buildModel([]), "US", 100), null);
  assert.equal(model.events.length, 0);
});
test("native colors retain opacity, and only bundled marker symbols resolve", () => {
  assert.equal(mapColor(-65536), "rgba(255,0,0,1)");
  assert.equal(mapColor(0), "rgba(0,0,0,0)");
  assert.ok(
    markerIcon({
      iconResource:
        "{E23427CAC80DA8B7}UI/Textures/Icons/icons_mapMarkersUI.imageset",
      iconName: "ambush-2",
    }),
  );
  assert.equal(
    markerIcon({ iconResource: "https://outside/image", iconName: "ambush" }),
    null,
  );
});
test("outside fill uses an even-odd viewport hole and zooms only world-width lines", () => {
  class FakePath {
    moveTo() {}
    lineTo() {}
    closePath() {}
    rect() {
      this.viewport = true;
    }
    addPath() {}
  }
  const previous = globalThis.Path2D;
  globalThis.Path2D = FakePath;
  try {
    const fills = [],
      strokes = [];
    const ctx = {
      save() {},
      restore() {},
      fill(p, rule) {
        fills.push([p.viewport, rule]);
      },
      stroke() {
        strokes.push(this.lineWidth);
      },
    };
    const base = {
      points: [0, 0, 10, 0, 10, 10],
      lineColor: -1,
      fillColor: -1,
      width: 3,
      closed: true,
    };
    drawMapShapes(
      ctx,
      [
        { ...base, inverted: true },
        { ...base, fillColor: 0, worldWidth: true },
      ],
      (p) => [p[0], -p[2]],
      2,
      100,
      100,
    );
    assert.deepEqual(fills, [[true, "evenodd"]]);
    assert.deepEqual(strokes, [3, 6]);
  } finally {
    globalThis.Path2D = previous;
  }
});
