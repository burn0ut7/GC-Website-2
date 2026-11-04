import test from "node:test";
import assert from "node:assert/strict";
import { buildModel, actorAt } from "../dist/playback-model.mjs";
const row = (sequence, timeMs, type, data) => ({sequence, timeMs, type, data});
const spawn = row(1, 0, "spawn", {entity:"a", kind:"player", playerIdentity:"p", prefab:"Character_US_Rifleman.et", position:[0,0,0]});
const voice = (n, t, type, channel) => row(n,t,type,{entity:"a", channel, source:"client-capture",validForMs:type === "voice-stop" ? 0 : 1500});
test("speaking icons switch, restore and reconstruct when seeking", () => {
  const m = buildModel([spawn, voice(2,100,"voice-start","local"), voice(3,300,"voice-start","radio"), voice(4,500,"voice-stop","radio"), voice(5,700,"voice-stop","local")]);
  const at = t => actorAt(m,m.actors.get("a"),t).voice;
  assert.equal(at(0),null); assert.equal(at(100),"local");
  assert.equal(at(300),"radio"); assert.equal(at(500),"local");
  assert.equal(at(700),null); assert.equal(at(350),"radio");
  assert.equal(m.events.length,0, "voice does not clutter combat activity");
});
test("missing stops expire; renewals extend speaking without future leakage", () => {
  const m = buildModel([spawn,voice(2,100,"voice-start","radio"),voice(3,600,"voice-refresh","radio")]);
  const at = t => actorAt(m,m.actors.get("a"),t).voice;
  assert.equal(at(1599),"radio"); assert.equal(at(2099),"radio");
  assert.equal(at(2100),null); assert.equal(at(50),null);
});
test("death and unoccupied slots cannot retain a speaking icon", () => {
  for (const [type,data] of [["death",{}],["ownership",{playerIdentity:""}]]) {
    const m = buildModel([spawn,voice(2,100,"voice-start","radio"),row(3,200,type,{entity:"a",...data})]);
    assert.equal(actorAt(m,m.actors.get("a"),200).voice,null);
  }
  const m=buildModel([spawn]);
  assert.equal(actorAt(m,m.actors.get("a"),500).voice,null,"old recordings remain quiet");
});
test("new owner speech follows ownership and does not inherit previous voice", () => {
  const m=buildModel([spawn, voice(2,100,"voice-start","radio"),
    row(3,200,"ownership",{entity:"a",playerIdentity:"new-player"}),
    voice(4,250,"voice-start","local")]);
  const a=m.actors.get("a");
  assert.equal(actorAt(m,a,200).voice,null);
  assert.equal(actorAt(m,a,250).voice,"local");
  assert.equal(actorAt(m,a,150).voice,"radio");
});
