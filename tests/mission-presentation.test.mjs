import test from 'node:test';
import assert from 'node:assert/strict';
import {mapName, missionFactions, missionTypes, slotLabel} from '../dist/mission-presentation.mjs';

test('seven mission types', () => assert.deepEqual(Object.keys(missionTypes), ['COOP','TVT','COTVT','TRNG','LOL','TEST','BROKEN']));
test('map names come from world identity, not a stale manual label', () => {
  const metadata={world:'{1234567890123456}Worlds\\Example.ent',map:'Wrong map'};
  const maps=[{worldSuffix:'worlds/example.ent',name:'Example'}];
  assert.equal(mapName(metadata,maps),'Example');
  assert.equal(mapName(metadata,[]),null);
  assert.equal(mapName(metadata,[...maps,{...maps[0],name:'Conflicting'}]),null);
});
test('factions use observed world and slot keys, not old declarations or unused configured teams', () => {
  assert.deepEqual(missionFactions({forces:[{faction:'OLD',slots:20}],generated:{configuredFactions:['UNUSED'],factions:['USSR','FIA'],playerSlots:[{faction:'US',loadoutFaction:'USSR'}]}}),['USSR','FIA','US']);
  assert.deepEqual(missionFactions({}),[]);
});
test('loadout labels preserve authored names and handle game localization keys',()=>{
 assert.equal(slotLabel({name:'Medic',prefab:'x.et'}),'Medic');
 assert.equal(slotLabel({name:'#AR-Medic',prefab:'{123}Prefabs/Character_US_Medic.et'}),'Character US Medic');
});
