import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import {
  buildModel,
  actorAt,
  shotAt,
  bounds,
} from "../dist/playback-model.mjs";
const row = (sequence, timeMs, type, data) => ({
  sequence,
  timeMs,
  type,
  data,
});
const spawn = (id, kind = "player") => ({
  entity: id,
  kind,
  playerIdentity: kind === "player" ? "p" : "",
  prefab: "Vehicles/M998.et",
  position: [0, 0, 0],
});
test("seek reconstructs stance, death and despawn without leaking future state", () => {
  const m = buildModel([
    row(1, 0, "spawn", spawn("a")),
    row(2, 100, "state", { entity: "a", position: [10, 0, 0], stance: 2 }),
    row(3, 200, "death", { entity: "a", position: [12, 0, 0] }),
    row(4, 300, "despawn", { entity: "a" }),
  ]);
  const a = m.actors.get("a");
  assert.equal(actorAt(m, a, 300), null);
  assert.equal(actorAt(m, a, 200).dead, true);
  assert.equal(actorAt(m, a, 100).dead, false);
  assert.equal(actorAt(m, a, 100).data.stance, 2);
  assert.equal(actorAt(m, a, -1), null);
  assert.equal(actorAt(m, a, 0).data.stance, undefined);
});
test("vehicle occupancy and names survive forward and backward seeks", () => {
  const m = buildModel([
    row(1, 0, "spawn", spawn("a")),
    row(2, 0, "spawn", spawn("v", "vehicle")),
    row(3, 100, "seat", { entity: "a", vehicle: "v", seatIndex: 1 }),
    row(4, 200, "seat", { entity: "a", vehicle: "", seatIndex: -1 }),
  ]);
  const a = m.actors.get("a");
  assert.equal(actorAt(m, a, 150).data.vehicle, "v");
  assert.equal(m.actors.get("v").name, "M998");
  assert.equal(actorAt(m, a, 250).data.vehicle, "");
  assert.equal(actorAt(m, a, 150).data.seatIndex, 1);
});
test("shots never use future impact; damage endpoint is not a terrain impact", () => {
  const m = buildModel([
    row(1, 0, "shot", {
      entity: "a",
      projectileId: "s",
      origin: [0, 0, 0],
      velocity: [100, 0, 0],
    }),
    row(2, 100, "damage", {
      entity: "b",
      projectileId: "s",
      position: [10, 0, 0],
    }),
    row(3, 200, "impact", { projectileId: "s", position: [12, 0, 0] }),
  ]);
  const s = m.shots.get("s");
  assert.equal(shotAt(s, -1), null);
  assert.equal(shotAt(s, 0).to[0], 60);
  assert.equal(shotAt(s, 100).to[0], 10);
  assert.equal(shotAt(s, 100).confirmed, false);
  assert.equal(shotAt(s, 4000), null);
});
test("only short continuous samples interpolate", () => {
  const m = buildModel([
    row(1, 0, "spawn", spawn("a")),
    row(2, 200, "state", { entity: "a", position: [10, 0, 0] }),
    row(3, 400, "state", {
      entity: "a",
      position: [30, 0, 0],
      discontinuity: true,
    }),
    row(4, 10000, "state", { entity: "a", position: [100, 0, 0] }),
  ]);
  const a = m.actors.get("a");
  assert.equal(actorAt(m, a, 100).position[0], 5);
  assert.equal(actorAt(m, a, 300).position[0], 10);
  assert.equal(actorAt(m, a, 5000).position[0], 30);
  assert.equal(actorAt(m, a, 100, false).position[0], 0);
});
test("gap blocks interpolation", () => {
  const m = buildModel([
    row(1, 0, "spawn", spawn("a")),
    row(2, 100, "gap", { fromMs: 0, untilMs: 150 }),
    row(3, 200, "state", { entity: "a", position: [10, 0, 0] }),
  ]);
  assert.equal(actorAt(m, m.actors.get("a"), 100).inferred, false);
});
if (fs.existsSync("artifacts/playback-fixture/records.json"))
  test("real Workbench capture: 59 shots, 3 deaths, 3 stances, complete seekable actors", () => {
    const records = JSON.parse(
        fs.readFileSync("artifacts/playback-fixture/records.json"),
      ),
      players = JSON.parse(
        fs.readFileSync("artifacts/playback-fixture/players.json"),
      ).players,
      m = buildModel(records, players);
    assert.equal(records.length, 495);
    assert.equal(m.shots.size, 59);
    assert.equal(m.events.filter((e) => e.type === "death").length, 3);
    assert.deepEqual(
      new Set(
        records.filter((r) => r.type === "state").map((r) => r.data.stance),
      ),
      new Set([0, 1, 2]),
    );
    for (const a of m.actors.values()) {
      assert(actorAt(m, a, records.at(-1).timeMs));
      assert.equal(actorAt(m, a, a.spawn.timeMs - 1), null);
    }
    assert(bounds(m).every(Number.isFinite));
    console.log("Real capture bounds:", bounds(m));
  });
test("latest vehicle transform supersedes spawn position without interpolation", () => {
  const m = buildModel([
    row(1, 0, "spawn", spawn("v", "vehicle")),
    row(2, 200, "vehicle-state", {
      entity: "v",
      transform: [1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 0, 20],
    }),
  ]);
  assert.deepEqual(
    actorAt(m, m.actors.get("v"), 250, false).position,
    [10, 0, 20],
  );
  assert.deepEqual(
    actorAt(m, m.actors.get("v"), 250, true).position,
    [10, 0, 20],
  );
});
test("impact supersedes damage only when observed and delayed detonation has its own lifetime", () => {
  const m = buildModel([
    row(1, 0, "grenade", {
      entity: "a",
      projectileId: "g",
      origin: [0, 0, 0],
      velocity: [10, 0, 0],
    }),
    row(2, 100, "damage", { projectileId: "g", position: [10, 0, 0] }),
    row(3, 5000, "detonation", { projectileId: "g", position: [50, 0, 0] }),
  ]);
  const s = m.shots.get("g");
  assert.equal(shotAt(s, 100).confirmed, false);
  assert.equal(shotAt(s, 4000), null);
  assert.equal(shotAt(s, 5000).confirmed, true);
  assert.deepEqual(shotAt(s, 5000).to, [50, 0, 0]);
  assert.equal(shotAt(s, 8001), null);
});
test("unset damage hit location never creates a line to origin", () => {
  const m = buildModel([
    row(1, 0, "shot", {
      entity: "a",
      projectileId: "s",
      origin: [10, 0, 10],
      velocity: [1, 0, 0],
    }),
    row(2, 100, "damage", { projectileId: "s", position: [0, 0, 0] }),
  ]);
  assert.match(shotAt(m.shots.get("s"), 100).evidence, /direction/);
});
import { eventActors } from "../dist/playback-model.mjs";
test("native contact arriving before shot remains linked without revealing future shots", () => {
  const m = buildModel([
    row(1, 100, "impact", { projectileId: "early", position: [5, 0, 0] }),
    row(2, 101, "shot", {
      entity: "a",
      projectileId: "early",
      origin: [1, 0, 0],
      velocity: [1, 0, 0],
    }),
  ]);
  assert.equal(m.shots.get("early").points.length, 1);
  assert.equal(shotAt(m.shots.get("early"), 100), null);
  assert.deepEqual(shotAt(m.shots.get("early"), 101).to, [5, 0, 0]);
});
test("successive contacts connect observed points without drawing through a ricochet", () => {
  const m = buildModel([
    row(1, 0, "shot", {
      entity: "a",
      projectileId: "s",
      origin: [0, 0, 0],
      velocity: [1, 0, 0],
    }),
    row(2, 100, "impact", { projectileId: "s", position: [5, 0, 0] }),
    row(3, 200, "impact", { projectileId: "s", position: [5, 0, 5] }),
  ]);
  assert.deepEqual(shotAt(m.shots.get("s"), 150).from, [0, 0, 0]);
  assert.deepEqual(shotAt(m.shots.get("s"), 200).from, [5, 0, 0]);
  assert.deepEqual(shotAt(m.shots.get("s"), 200).to, [5, 0, 5]);
  assert.match(shotAt(m.shots.get("s"), 200).evidence, /not flight path/);
});
test("impact-only events inherit their recorded shooter for actor filtering", () => {
  const m = buildModel([
    row(1, 0, "shot", {
      entity: "a",
      projectileId: "s",
      origin: [0, 0, 0],
      velocity: [1, 0, 0],
    }),
    row(2, 200, "impact", { projectileId: "s", position: [5, 0, 0] }),
  ]);
  assert.deepEqual(eventActors(m.events[1], m), ["a"]);
});
import { factionKey, factionColor } from "../dist/playback-model.mjs";
test("faction changes follow seek time; unrecorded factions remain unknown", () => {
  const m = buildModel([
    row(1, 0, "spawn", { ...spawn("a"), faction: "US" }),
    row(2, 100, "appearance", { entity: "a", faction: "USSR" }),
    row(3, 200, "appearance", { entity: "a", faction: "" }),
  ]);
  const a = m.actors.get("a");
  assert.equal(actorAt(m, a, 150).faction, "USSR");
  assert.equal(actorAt(m, a, 50).faction, "US");
  assert.equal(actorAt(m, a, 250).faction, "Unknown");
  assert.equal(factionKey({}), "Unknown");
  assert.notEqual(factionColor("US"), factionColor("USSR"));
  assert.equal(factionColor("custom"), factionColor("Unknown"));
});
import { eventActorAt } from "../dist/playback-model.mjs";
test("stationary AI remains visible from its initial spawn without movement samples", () => {
  const m = buildModel([
    row(1, 0, "spawn", {
      ...spawn("idle", "ai"),
      playerIdentity: "",
      position: [12, 0, 34],
    }),
  ]);
  assert.deepEqual(
    actorAt(m, m.actors.get("idle"), 60000).position,
    [12, 0, 34],
  );
  assert.equal(actorAt(m, m.actors.get("idle"), 60000).kind, "ai");
});
test("vacated player bodies keep position and player filter but lose former names", () => {
  const m = buildModel(
    [
      row(1, 0, "spawn", {
        ...spawn("slot", "player"),
        playerIdentity: "first",
        position: [10, 0, 20],
      }),
      row(2, 1000, "ownership", { entity: "slot", playerIdentity: "" }),
      row(3, 2000, "ownership", { entity: "slot", playerIdentity: "next" }),
    ],
    [
      { identity: "first", name: "First occupant" },
      { identity: "next", name: "Next occupant" },
    ],
  );
  const a = m.actors.get("slot");
  assert.equal(actorAt(m, a, 500).name, "First occupant");
  assert.deepEqual(actorAt(m, a, 1500).position, [10, 0, 20]);
  assert.equal(actorAt(m, a, 1500).kind, "player");
  assert.doesNotMatch(actorAt(m, a, 1500).name, /First occupant|Next occupant/);
  assert.equal(actorAt(m, a, 2500).name, "Next occupant");
  assert.equal(actorAt(m, a, 500).name, "First occupant");
});
test("shot and delayed impact filters keep firing faction after switches and despawn", () => {
  const m = buildModel([
    row(1, 0, "spawn", { ...spawn("a"), faction: "US" }),
    row(2, 100, "shot", {
      entity: "a",
      projectileId: "s",
      origin: [0, 0, 0],
      velocity: [1, 0, 0],
    }),
    row(3, 1000, "appearance", { entity: "a", faction: "USSR" }),
    row(4, 1500, "despawn", { entity: "a" }),
    row(5, 5000, "impact", { projectileId: "s", position: [5, 0, 0] }),
  ]);
  const a = m.actors.get("a");
  assert.equal(actorAt(m, a, 1200).faction, "USSR");
  assert.equal(eventActorAt(m, a, m.rows[1]).faction, "US");
  assert.equal(eventActorAt(m, a, m.rows[4]).faction, "US");
  assert.equal(actorAt(m, a, 5000), null);
  assert.equal(
    eventActorAt(m, a, row(6, 6000, "damage", { entity: "a" })).faction,
    "USSR",
  );
});

test("facing uses XYZ yaw and follows the shortest turn across north", () => {
  const m = buildModel([
    row(1, 0, "spawn", { ...spawn("a"), anglesDegrees: [12, 350, 8] }),
    row(2, 1000, "state", {
      entity: "a",
      position: [0, 0, 0],
      anglesDegrees: [12, 10, 8],
    }),
  ]);
  const a = m.actors.get("a");
  assert.equal(actorAt(m, a, 0).heading, 350);
  assert.equal(actorAt(m, a, 500).heading, 0);
  assert.equal(actorAt(m, a, 1000).heading, 10);
  assert.equal(actorAt(m, a, 500, false).heading, 350);
  assert.equal(actorAt(m, a, 250).heading, 355);
});

test("vehicle facing reads legacy forward axis and never invents missing headings", () => {
  const m = buildModel([
    row(1, 0, "spawn", {
      ...spawn("v", "vehicle"),
      transform: [0, 0, -1, 0, 1, 0, 1, 0, 0, 0, 0, 0],
    }),
    row(2, 500, "vehicle-state", {
      entity: "v",
      position: [2, 0, 0],
      anglesDegrees: [0, 180, 0],
      discontinuity: true,
    }),
    row(3, 0, "spawn", spawn("unknown")),
  ]);
  assert.equal(actorAt(m, m.actors.get("v"), 250).heading, 90);
  assert.equal(actorAt(m, m.actors.get("v"), 500).heading, 180);
  assert.equal(actorAt(m, m.actors.get("unknown"), 500).heading, null);
});

test("facing does not interpolate over gaps or long sample intervals", () => {
  const records = [
    row(1, 0, "spawn", { ...spawn("a"), anglesDegrees: [0, 20, 0] }),
    row(2, 500, "state", {
      entity: "a",
      position: [0, 0, 0],
      anglesDegrees: [0, 90, 0],
    }),
    row(3, 100, "gap", { fromMs: 100, untilMs: 200 }),
  ];
  let m = buildModel(records);
  assert.equal(actorAt(m, m.actors.get("a"), 300).heading, 20);
  records[1].timeMs = 2000;
  m = buildModel(records.slice(0, 2));
  assert.equal(actorAt(m, m.actors.get("a"), 1000).heading, 20);
});
