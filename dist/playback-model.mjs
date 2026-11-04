/** Recorded facts only. Interpolation is limited to adjacent, continuous samples. */
export const position = (d) =>
  d.position ?? (d.transform?.length === 12 ? d.transform.slice(9, 12) : null);
// GetAngles is XYZ rotation: Y is yaw. Legacy matrices store forward in row 2.
export function heading(data) {
  if (data.transform?.length === 12) {
    const [x, z] = [data.transform[6], data.transform[8]];
    return Number.isFinite(x) && Number.isFinite(z) && Math.hypot(x, z) > 1e-6
      ? ((Math.atan2(x, z) * 180) / Math.PI + 360) % 360
      : null;
  }
  const yaw = data.anglesDegrees?.[1];
  return Number.isFinite(yaw) ? ((yaw % 360) + 360) % 360 : null;
}
export const label = (p) =>
  (p ?? "").split("/").pop().replace(/\.et$/i, "").replace(/_/g, " ") ||
  "Unknown";
export const factionKey = (data) => data.faction?.trim() || "Unknown";
// Viewer defaults, not inferred alliances or engine faction colors.
export function factionColor(key) {
  const defaults = {
    US: "#79b8ff",
    USSR: "#f08080",
    FIA: "#94cf82",
    CIV: "#c7a4e8",
  };
  return Object.hasOwn(defaults, key) ? defaults[key] : "#c7cbbb";
}
export function latest(rows, time) {
  let lo = 0,
    hi = rows.length;
  while (lo < hi) {
    const m = (lo + hi) >> 1;
    if (rows[m].timeMs <= time) lo = m + 1;
    else hi = m;
  }
  return lo - 1;
}
export function buildModel(records, players = []) {
  const rows = [...records].sort((a, b) => a.sequence - b.sequence),
    actors = new Map(),
    mapStates = new Map(),
    shots = new Map(),
    events = [],
    gaps = [];
  const names = new Map(players.map((p) => [p.identity, p.name]));
  // Native rewind can deliver a contact before its character shot callback.
  // Index identities first; rendering still checks the recorded event times.
  for (const row of rows)
    if (row.type === "shot" || row.type === "grenade")
      shots.set(row.data.projectileId, { shot: row, points: [] });
  for (const row of rows) {
    const d = row.data;
    if (row.type === "map-state") {
      if (!mapStates.has(d.faction)) mapStates.set(d.faction, []);
      mapStates.get(d.faction).push(row);
    }
    if (row.type === "spawn")
      actors.set(d.entity, {
        id: d.entity,
        spawn: row,
        frames: [],
        states: [],
        positions: [],
        voice: { local: [], radio: [] },
        name:
          names.get(d.playerIdentity) ||
          `${d.kind === "ai" ? "AI · " : ""}${label(d.prefab)}${d.kind === "ai" ? " · " + d.entity : ""}`,
        kind: d.kind,
      });
    const actor = actors.get(d.entity);
    if (actor) {
      actor.frames.push(row);
      if (["voice-start", "voice-refresh", "voice-stop"].includes(row.type))
        actor.voice[d.channel]?.push(row);
      if (["ownership", "death", "despawn"].includes(row.type)) {
        const stop = { ...row, type: "voice-stop" };
        actor.voice.local.push(stop);
        actor.voice.radio.push(stop);
      }
      if (
        position(d) &&
        ["spawn", "state", "vehicle-state", "death"].includes(row.type)
      )
        actor.positions.push(row);
      if (
        [
          "spawn",
          "state",
          "vehicle-state",
          "death",
          "despawn",
          "seat",
          "equipment",
          "ownership",
          "appearance",
        ].includes(row.type)
      ) {
        const prev = actor.states.at(-1);
        actor.states.push({
          timeMs: row.timeMs,
          data: { ...prev?.data, ...d },
          dead: prev?.dead || row.type === "death",
          despawn: prev?.despawn || row.type === "despawn",
          wasPlayer:
            prev?.wasPlayer || d.kind === "player" || !!d.playerIdentity,
        });
      }
    }
    if (
      ["impact", "detonation", "damage", "projectile-sample"].includes(
        row.type,
      ) &&
      d.projectileId &&
      shots.has(d.projectileId)
    )
      shots.get(d.projectileId).points.push(row);
    if (
      [
        "shot",
        "grenade",
        "damage",
        "death",
        "seat",
        "impact",
        "detonation",
        "gap",
      ].includes(row.type)
    )
      events.push(row);
    if (row.type === "gap") gaps.push(row);
  }
  events.sort((a, b) => a.timeMs - b.timeMs || a.sequence - b.sequence);
  return { rows, actors, shots, events, gaps, players, names, mapStates };
}
export function actorAt(
  model,
  actor,
  time,
  interpolate = true,
  includeDespawn = false,
) {
  if (time < actor.spawn.timeMs) return null;
  const { data, dead, despawn, wasPlayer } =
    actor.states[latest(actor.states, time)];
  if (despawn && !includeDespawn) return null;
  const idx = latest(actor.positions, time),
    prev = actor.positions[idx],
    next = actor.positions[idx + 1];
  // A transform sample must supersede a spawn's position field.
  let p = prev ? position(prev.data) : null;
  let facing = prev ? heading(prev.data) : null;
  let inferred = false;
  const gap = model.gaps.some(
    (g) => time >= g.data.fromMs && time <= g.data.untilMs,
  );
  const crossesGap =
    prev &&
    next &&
    model.gaps.some(
      (g) => g.data.fromMs <= next.timeMs && g.data.untilMs >= prev.timeMs,
    );
  if (
    interpolate &&
    !dead &&
    !gap &&
    !crossesGap &&
    prev &&
    next &&
    !next.data.discontinuity &&
    next.timeMs - prev.timeMs <= 1000 &&
    next.timeMs > prev.timeMs &&
    next.type !== "death"
  ) {
    const a = position(prev.data),
      b = position(next.data),
      f = (time - prev.timeMs) / (next.timeMs - prev.timeMs);
    p = a.map((v, i) => v + (b[i] - v) * f);
    const nextFacing = heading(next.data);
    if (facing !== null && nextFacing !== null)
      facing =
        (facing + (((nextFacing - facing + 540) % 360) - 180) * f + 360) % 360;
    inferred = f > 0;
  }
  const kind =
    data.kind === "vehicle" ? "vehicle" : wasPlayer ? "player" : "ai";
  return {
    id: actor.id,
    data,
    position: p,
    heading: facing,
    dead,
    kind,
    name: actorNameAt(model, actor, time),
    faction: factionKey(data),
    inferred,
    gap,
    voice: !dead && !despawn && !gap && data.playerIdentity
      ? voiceAt(actor, time) : null,
    lastTime: prev?.timeMs ?? actor.spawn.timeMs,
  };
}
export function voiceAt(actor, time) {
  for (const channel of ["radio", "local"]) {
    const rows = actor.voice?.[channel] ?? [];
    const row = rows[latest(rows, time)];
    if (row && row.type !== "voice-stop" && time < row.timeMs + row.data.validForMs)
      return channel;
  }
  return null;
}
export function actorNameAt(model, actor, time) {
  const index = latest(actor.states, time);
  const identity = index >= 0 ? actor.states[index].data.playerIdentity : "";
  if (identity) return model.names.get(identity) || "Player";
  return label(actor.spawn.data.prefab)
    .replace(/^Character\s+/i, "")
    .replace(/^USSR\s+/i, "");
}
export function shotAt(shot, time, ttl = 3000) {
  const r = shot.shot,
    d = r.data;
  if (r.timeMs > time) return null;
  const impact = shot.points.findLast(
    (p) => p.timeMs <= time && ["impact", "detonation"].includes(p.type),
  );
  const damage = shot.points.findLast(
    (p) =>
      p.timeMs <= time &&
      // Legacy engine damage callbacks can supply an unset zero vector.
      p.type === "damage" &&
      p.data.position?.some((v) => v !== 0),
  );
  const hit = impact ?? damage;
  const contacts = shot.points.filter(
    (p) => p.timeMs <= time && ["impact", "detonation"].includes(p.type),
  );
  const previousContact = contacts.length > 1 ? contacts.at(-2) : null;
  if (hit && time - hit.timeMs <= ttl)
    return {
      from:
        impact && previousContact ? previousContact.data.position : d.origin,
      to: hit.data.position,
      confirmed: hit.type !== "damage",
      evidence:
        hit.type === "damage"
          ? "Recorded damage point"
          : "Recorded contact · connecting segment, not flight path",
      actor: d.entity,
    };
  if (time - r.timeMs > ttl) return null;
  const v = d.velocity,
    len = Math.hypot(v[0], v[2]);
  if (!len)
    return {
      from: d.origin,
      to: d.origin,
      confirmed: false,
      evidence: "Shot origin; direction unavailable",
      actor: d.entity,
    };
  return {
    from: d.origin,
    to: [
      d.origin[0] + (v[0] / len) * 60,
      d.origin[1],
      d.origin[2] + (v[2] / len) * 60,
    ],
    confirmed: false,
    evidence: "Initial direction only · landing unknown",
    actor: d.entity,
  };
}
export function eventActors(row, model) {
  const d = row.data;
  const shooter = model?.shots.get(d.projectileId)?.shot.data.entity;
  return [d.entity, d.attacker, d.killer, d.vehicle, d.target, shooter].filter(
    Boolean,
  );
}
// Evidence retains the shooter's firing-time affiliation, even after switching
// factions or despawning. Other participants use their last state at event time.
export function eventActorAt(model, actor, row) {
  const shot = model.shots.get(row.data.projectileId)?.shot;
  const time = shot?.data.entity === actor.id ? shot.timeMs : row.timeMs;
  return actorAt(model, actor, time, false, true);
}
export function bounds(model) {
  let x = Infinity,
    z = Infinity,
    X = -Infinity,
    Z = -Infinity;
  for (const actor of model.actors.values())
    for (const row of actor.positions) {
      const p = position(row.data);
      if (!p) continue;
      x = Math.min(x, p[0]);
      z = Math.min(z, p[2]);
      X = Math.max(X, p[0]);
      Z = Math.max(Z, p[2]);
    }
  for (const states of model.mapStates?.values() ?? []) {
    for (const { data } of states) {
      for (const shape of data.shapes) {
        for (let i = 0; i < shape.points.length; i += 2) {
          x = Math.min(x, shape.points[i]);
          X = Math.max(X, shape.points[i]);
          z = Math.min(z, shape.points[i + 1]);
          Z = Math.max(Z, shape.points[i + 1]);
        }
      }
      for (const marker of data.markers) {
        x = Math.min(x, marker.position[0]);
        X = Math.max(X, marker.position[0]);
        z = Math.min(z, marker.position[2]);
        Z = Math.max(Z, marker.position[2]);
      }
    }
  }
  return Number.isFinite(x)
    ? [x - 30, z - 30, X + 30, Z + 30]
    : [0, 0, 1000, 1000];
}

export function mapStateAt(model, faction, time) {
  const states = model.mapStates.get(faction) ?? [];
  const index = latest(states, time);
  return index < 0 ? null : states[index].data;
}
