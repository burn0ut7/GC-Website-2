import test from 'node:test';import assert from 'node:assert/strict';import fs from 'node:fs';
import {prepareMapSymbols,drawMapSymbols} from '../dist/playback-topography.mjs';
const data=JSON.parse(fs.readFileSync(new URL('../dist/maps/arland-symbols.json',import.meta.url)));
test('live symbols use real configured atlas assets',()=>{const symbols=prepareMapSymbols(data);assert.equal(symbols.length,80);assert.equal(symbols.filter(s=>s.type==='MDT_BUNKER').length,20);for(const s of symbols)assert.ok(fs.existsSync(new URL('../dist'+s.style.url,import.meta.url)));assert.deepEqual(data.unmapped,{});});
test('position, rotation, culling and visibility',()=>{const s={type:'MDT_BUNKER',x:10,z:20,yaw:90,style:{url:'/icons/reforger/topo-MDT_BUNKER.png',size:40,viewLayer:2,rotate:true}};let drawn=0,angle=0,pos;const ctx={save(){},restore(){},translate(x,y){pos=[x,y]},rotate(a){angle=a},drawImage(){drawn++}};drawMapSymbols(ctx,[s],{x:0,z:0,scale:2},100,100,()=>({}));assert.deepEqual(pos,[70,10]);assert.equal(angle,Math.PI/2);assert.equal(drawn,1);drawMapSymbols(ctx,[s],{x:0,z:0,scale:.01},100,100,()=>({}));assert.equal(drawn,1);drawMapSymbols(ctx,[s],{x:1000,z:1000,scale:2},100,100,()=>({}));assert.equal(drawn,1);});


test('landmarks stay hidden at overview zoom with matching drawing and selection',async()=>{
  const {findMapObject}=await import('../dist/playback-buildings.mjs');
  for(const [layer,threshold] of [1,2,3,4,5,6].map(layer=>[layer,1/(1.15**3)])) {
    const symbol={x:0,z:0,yaw:0,model:'test',style:{viewLayer:layer,size:30}};
    for(const [scale,visible] of [[threshold*.99,false],[threshold,true]]) {
      let drawn=0;const ctx={save(){},restore(){},translate(){},drawImage(){drawn++}};
      drawMapSymbols(ctx,[symbol],{x:0,z:0,scale},100,100,()=>({}));
      assert.equal(drawn,Number(visible));
      assert.equal(!!findMapObject([symbol],0,0,scale),visible);
    }
  }
  const {mapSymbolVisible}=await import('../dist/playback-symbol-visibility.mjs');
  assert.equal(mapSymbolVisible({viewLayer:0},10),false);
  assert.equal(mapSymbolVisible(data.styles.MDT_FUELSTATION,.1),false);
  assert.equal(mapSymbolVisible(data.styles.MDT_FUELSTATION,.25),false);
  assert.equal(mapSymbolVisible(data.styles.MDT_FUELSTATION,.5),false);
  assert.equal(mapSymbolVisible(data.styles.MDT_FUELSTATION,.75),true);
  assert.equal(mapSymbolVisible(data.styles.MDT_FUELSTATION,1),true);
  assert.equal(mapSymbolVisible(data.styles.MDT_BUNKER,.25),false);
});
