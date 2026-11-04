import test from 'node:test';
import assert from 'node:assert/strict';
import {unitSymbolSize} from '../dist/playback-unit-scale.mjs';
test('world-scale infantry uses 2m footprint with a small overview visibility floor',()=>{
 for(const [scale,diameter] of [[.1,3],[.3,3],[1,3],[4,8],[10,20],[30,60]]){
 const s=unitSymbolSize('player',null,scale,true,14);assert.equal(s.radius*2,diameter);assert.equal(s.factor,diameter/28);
 }
 assert.equal(unitSymbolSize('ai',null,4,true,7).radius,4);
});
test('vehicles keep approximate category sizes and disabling restores original symbols',()=>{
 assert.equal(unitSymbolSize('vehicle',{name:'Helicopter'},1,true,19).radius,7);
 assert.equal(unitSymbolSize('vehicle',{name:'Offroad'},1,true,19).radius,2.5);
 assert.deepEqual(unitSymbolSize('player',null,.1,false,14),{radius:14,factor:1,simple:false});
});
