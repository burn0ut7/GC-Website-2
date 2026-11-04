import test from 'node:test';
import assert from 'node:assert/strict';
import {findBuilding, buildingModel, buildingModels, findMapObject} from '../dist/playback-buildings.mjs';

const triangle = {layer:'buildings', bounds:[0,0,10,10], points:[0,0,10,0,0,10]};
test('wall footprints are clickable only when rendered at this zoom', () => {
  const wall = {...triangle, layer:'walls'};
  assert.equal(findBuilding([wall],2,2,1),wall);
  assert.equal(findBuilding([wall],2,2,.2),null);
  assert.equal(findBuilding([wall],9,9,1),null);
});
test('direct source binding avoids geometry lookup and preserves uncertainty', () => {
  const catalog={version:1,models:{source:{name:'Mod object'}},get buildings(){throw Error('Legacy search must not run');}};
  assert.equal(buildingModels(catalog,{model:'source'})[0].name,'Mod object');
  assert.equal(buildingModels(catalog,{models:['source'],ambiguous:true})[0].ambiguous,true);
});
test('building hit uses the footprint rather than its bounding box', () => {
  assert.equal(findBuilding([triangle],2,2),triangle);
  assert.equal(findBuilding([triangle],9,9),null);
  assert.equal(findBuilding([{...triangle,layer:'roads'}],2,2),null);
});
test('facility hit testing respects zoom visibility and model choices remain explicit',()=>{
  const symbol={x:10,z:10,model:'a',style:{viewLayer:2,size:16}};
  assert.equal(findMapObject([symbol],10,10,2),symbol);
  assert.equal(findMapObject([symbol],10,10,.1),null);
  assert.equal(findMapObject([symbol],100,100,1),null);
  const catalog={version:1,buildings:[{points:triangle.points,models:['a','b']}],models:{a:{name:'House'},b:{name:'Shed'}}};
  assert.deepEqual(buildingModels(catalog,triangle).map(m=>m.name),['House','Shed']);
  assert.equal(buildingModels(catalog,symbol)[0].name,'House');
});
test('preview requires the exact footprint and a packaged model image', () => {
  const catalog = {version:1,buildings:[{points:triangle.points,model:'a'}],models:{a:{name:'House',image:'/maps/buildings/0123456789abcdef.webp'}}};
  assert.equal(buildingModel(catalog,triangle).name,'House');
  catalog.models.a.image='/map-packages/everon/'+ 'a'.repeat(64) + '/maps/buildings/0123456789abcdef.webp';
  assert.equal(buildingModel(catalog,triangle).image,catalog.models.a.image);
  catalog.models.a.image='/map-previews/'+'b'.repeat(64)+'.webp';
  assert.equal(buildingModel(catalog,triangle).image,catalog.models.a.image);
  assert.equal(buildingModel(catalog,{points:[0,0,11,0,0,10]}),null);
  catalog.models.a.image='https://external.example/image.png';
  assert.equal(buildingModel(catalog,triangle).image,null);
});
