export const missionTypes = {COOP:'CO-OP',TVT:'TVT',COTVT:'COTVT',TRNG:'Training',LOL:'LOL',TEST:'Testing',BROKEN:'Broken'};

export function mapName(metadata, maps) {
  const world = (metadata?.world || '').replaceAll('\\', '/').toLowerCase();
  const matches = maps.filter(m => m.worldSuffix && world.endsWith(m.worldSuffix.replaceAll('\\', '/').toLowerCase()));
  const names = [...new Set(matches.map(m => m.name).filter(Boolean))];
  // An ambiguous or unknown world must not acquire a guessed terrain name.
  return names.length === 1 ? names[0] : null;
}

export function missionFactions(metadata) {
  const result = [], known = new Set();
  // Configured factions may include unused teams. Show observed world/slot keys only.
  for (const faction of [...(metadata.generated?.factions || []), ...(metadata.generated?.playerSlots || []).flatMap(s=>[s.faction,s.loadoutFaction])].filter(Boolean)) {
    if (!known.has(faction.toLowerCase())) { result.push(faction); known.add(faction.toLowerCase()); }
  }
  return result;
}

export function slotLabel(slot) {
  if (slot.name && !slot.name.startsWith('#')) return slot.name;
  return (slot.prefab || '').split('/').pop()?.replace(/\.et$/i,'').replaceAll('_',' ') || 'Unknown loadout';
}

export function missionLabels(metadata = {}) {
  return [...new Set([metadata.startTime, metadata.era, ...(metadata.objectives || []),
    ...(metadata.assetTypes || []), ...missionFactions(metadata), ...(metadata.mods || []),
    ...(metadata.tags || [])].filter(Boolean))];
}
