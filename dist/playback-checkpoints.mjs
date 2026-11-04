import { latest, factionKey, actorNameAt } from './playback-model.mjs';

// Use ownership and faction at the event time, not the actor's final state.
export function combatCheckpoints(model) {
  const points = [];
  for (const event of model.events) {
    if (event.type !== 'death') continue;
    for (const [kind, id] of [['death', event.data.entity], ['kill', event.data.killer]]) {
      if (kind === 'kill' && id === event.data.entity) continue;
      const actor = model.actors.get(id);
      if (!actor) continue;
      const state = actor.states[latest(actor.states, event.timeMs)];
      if (!state?.data.playerIdentity) continue;
      points.push({kind, timeMs:event.timeMs, faction:factionKey(state.data),
        name:actorNameAt(model, actor, event.timeMs)});
    }
  }
  return points;
}
