import { mapSymbolVisible } from "./playback-symbol-visibility.mjs";
// Only invoked on selection, never in the animation/render loop.
export function findBuilding(features, x, z, scale = 1) {
  const hits = (features ?? []).filter(f => {
    if (!['buildings', 'walls'].includes(f.layer) || !f.points) return false;
    if (f.layer === 'walls' && scale < .4) return false;
    const b = f.bounds;
    if (x < b[0] || z < b[1] || x > b[2] || z > b[3]) return false;
    let inside = false;
    const p = f.points;
    for (let i = 0, j = p.length - 2; i < p.length; j = i, i += 2) {
      if ((p[i+1] > z) !== (p[j+1] > z) &&
          x < (p[j]-p[i])*(z-p[i+1])/(p[j+1]-p[i+1])+p[i]) inside = !inside;
    }
    return inside;
  });
  // Small additions win over a containing main structure.
  hits.sort((a,b) => (a.bounds[2]-a.bounds[0])*(a.bounds[3]-a.bounds[1]) -
    (b.bounds[2]-b.bounds[0])*(b.bounds[3]-b.bounds[1]));
  return hits[0] ?? null;
}

export function buildingModels(catalog, feature) {
  if (catalog?.version !== 1 || !feature) return [];
  const match = feature.model || feature.models ? null : catalog.buildings?.find(b=>b.points.join(',')===feature.points?.join(','));
  return (feature.model ? [feature.model] : feature.models ?? match?.models ?? (match?.model ? [match.model] : []))
    .map(model=>buildingModel(catalog,{model})).filter(Boolean)
    .map(model=>({...model, ambiguous: feature.ambiguous === true || match?.ambiguous === true || !!feature.models}));
}

export function buildingModel(catalog, feature) {
  if (catalog?.version !== 1 || !feature) return null;
  const key = feature.points?.join(',');
  const match = feature.model ? null : catalog.buildings?.find(b => b.points.join(',') === key);
  const model = catalog.models?.[feature.model ?? match?.model];
  if (!model || typeof model.name !== 'string') return null;
  return {name:model.name, image: /^\/(?:map-previews\/[a-f0-9]{64}\.webp|(?:map-packages\/[a-z0-9][a-z0-9-]{0,63}\/[a-f0-9]{64}\/)?maps\/buildings\/[a-f0-9]{16}\.webp)$/.test(model.image ?? '') ? model.image : null};
}

export function findMapObject(symbols, x, z, scale) {
  return (symbols ?? []).filter(s => s.model && mapSymbolVisible(s.style, scale))
    .map(s => ({s, distance: Math.hypot(s.x-x,s.z-z)*scale}))
    .filter(hit => hit.distance <= Math.max(8,hit.s.style.size*Math.max(.5,Math.min(1,scale))/2))
    .sort((a,b)=>a.distance-b.distance)[0]?.s ?? null;
}
