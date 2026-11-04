import test from 'node:test';
import assert from 'node:assert/strict';
import {buildTerrainMesh} from '../dist/playback-terrain-mesh.mjs';
import {TerrainVectors} from '../dist/playback-terrain-vectors.mjs';
const feature=(layer='buildings',kind=0,points=[0,0,10,0,10,10,0,10])=>({layer,kind,points});
const data={version:1,features:[feature(),feature('roads',1),feature('walls'),feature('powerlines',0,[0,20,100,20])]};
function canvas(){
 const calls=[],events={};
 const gl=new Proxy({calls,getShaderParameter:()=>true,getProgramParameter:()=>true,getAttribLocation:(_,k)=>['world','offset','tint','order'].indexOf(k),createBuffer:()=>({}),createShader:()=>({}),createProgram:()=>({})},{get:(o,k)=>k in o?o[k]:typeof k==='string'&&k.toUpperCase()===k?0:(...args)=>{calls.push([k,...args]);}});
 return{width:0,height:0,gl,events,getContext:()=>gl,addEventListener:(k,f)=>events[k]=f};
}
class WorkerStub {static all=[];constructor(){WorkerStub.all.push(this);}postMessage(m){this.message=m;}terminate(){this.terminated=true;}}
globalThis.Worker=WorkerStub;
function setup(){const c=canvas(),p=canvas(),errors=[];return{c,p,errors,r:new TerrainVectors(c,p,()=>{},e=>errors.push(e))};}
const view={x:50,z:25,scale:1},options={terrainRoads:true,terrainBuildings:true,terrainPower:true};
function load(r){r.update(data,view,800,600,2,options);const w=r.worker;w.onmessage({data:{generation:r.generation,...buildTerrainMesh(data)}});return w;}
test('concave polygons and reversed winding triangulate to the same measured area',()=>{
 const p=[0,0,10,0,10,4,4,4,4,10,0,10];
 for(const points of [p,p.reduce((a,_,i)=>i%2?a:[...p.slice(i,i+2),...a],[])]){
 const mesh=buildTerrainMesh({version:1,features:[feature('roads',1,points)]});const v=mesh.batches[0].vertices;let area=0;
 for(let i=0;i<v.length;i+=24)area+=Math.abs((v[i+8]-v[i])*(v[i+17]-v[i+1])-(v[i+16]-v[i])*(v[i+9]-v[i+1]))/2;
 assert.equal(area,64);assert.equal(mesh.featureCount,1);
 }
});
test('mesh retains layers, colors and constant pixel line widths without dropping coincident buildings',()=>{
 const mesh=buildTerrainMesh({...data,features:[...data.features,feature()]});
 assert.equal(mesh.featureCount,5);assert.deepEqual(new Set(mesh.batches.map(b=>b.layer)),new Set(['buildings','roads','walls','powerlines']));
 assert.equal(mesh.bytes,mesh.batches.reduce((s,b)=>s+b.vertices.byteLength,0));
 const power=mesh.batches.find(b=>b.layer==='powerlines').vertices;
 let max=0;for(let i=0;i<power.length;i+=8)max=Math.max(max,Math.hypot(power[i+2],power[i+3]));assert.ok(Math.abs(max-1.1)<.00001);
 assert.throws(()=>buildTerrainMesh({version:1,features:[feature('roads',1,[NaN,0,1,1])]}));
});
test('pan and zoom reuse uploaded geometry at every scale without workers or raster caches',()=>{
 const {r,c,p}=setup();const worker=load(r);assert.equal(worker.terminated,true);
 for(const scale of [.08,.2,.4,1,3,30]){r.update(data,{...view,scale},800,600,2,options);r.paintPower(null,{...view,scale},800,600,options);}
 const uploaded=c.gl.calls.filter(c=>c[0]==='bufferData').length;
 for(const scale of [.08,.2,.4,1,3,30]){r.update(data,{...view,x:60,scale},800,600,2,options);r.paintPower(null,{...view,x:60,scale},800,600,options);}
 assert.equal(c.gl.calls.filter(c=>c[0]==='bufferData').length,uploaded);assert.equal(uploaded,3);assert.equal(p.gl.calls.filter(c=>c[0]==='bufferData').length,1);
 assert.equal(r.worker,null);assert.equal(c.width,1600);assert.equal(c.height,1200);
});
test('layer switches and native zoom visibility retain buffers and clear hidden surfaces',()=>{
 const {r,c,p}=setup();load(r);r.update(data,view,800,600,1,options);r.paintPower(null,view,800,600,options);
 const draws=()=>c.gl.calls.filter(c=>c[0]==='drawArrays').length;const start=draws();
 r.update(data,view,800,600,1,{});assert.equal(draws(),start);
 r.update(data,{...view,scale:.1},800,600,1,options);assert.equal(draws()-start,2);
 const power=p.gl.calls.filter(c=>c[0]==='drawArrays').length;
 r.paintPower(null,{...view,scale:.01},800,600,options);assert.equal(p.gl.calls.filter(c=>c[0]==='drawArrays').length,power);
 assert.equal(p.gl.calls.some(c=>c[0]==='clear'),true);
});
test('map switching terminates old work, releases GPU buffers and ignores stale results',()=>{
 const {r,c}=setup();load(r);r.update(data,view,800,600,1,options);assert.ok(c.gl.calls.some(c=>c[0]==='bufferData'));
 const other={...data};r.update(other,view,800,600,1,options);const stale=r.worker;
 r.update(null,view,800,600,1,options);assert.equal(stale.terminated,true);assert.equal(r.batches.length,0);
 stale.onmessage({data:buildTerrainMesh(data)});assert.equal(r.batches.length,0);assert.ok(c.gl.calls.some(c=>c[0]==='deleteBuffer'));
});
test('context restoration rebuilds GPU resources from retained geometry',()=>{
 const {r,c}=setup();load(r);r.update(data,view,800,600,1,options);const n=c.gl.calls.filter(c=>c[0]==='bufferData').length;
 c.events.webglcontextlost({preventDefault(){}});r.update(data,view,800,600,1,options);assert.equal(c.gl.calls.filter(c=>c[0]==='bufferData').length,n);
 c.events.webglcontextrestored();r.update(data,view,800,600,1,options);assert.equal(c.gl.calls.filter(c=>c[0]==='bufferData').length,n*2);
});

test('depth preserves overlapping building order and global road-kind ordering across spatial batches',()=>{
 const mesh=buildTerrainMesh({version:1,features:[feature('buildings',0),feature('buildings',1),feature('buildings',0),feature('roads',2),feature('roads',1),feature('roads',2,[1000,0,1010,0,1010,10])]});
 const buildings=mesh.batches.filter(b=>b.layer==='buildings');
 const depths=b=>Array.from(b.vertices).filter((_,i)=>i%8===7);
 assert.ok(Math.min(...depths(buildings[0]))<depths(buildings[1])[0],'last kind0 must cover kind1');
 assert.ok(Math.max(...depths(buildings[0]))>depths(buildings[1])[0],'first kind0 must be beneath kind1');
 const roads=mesh.batches.filter(b=>b.layer==='roads');
 assert.ok(roads.filter(b=>b.kind===2).every(b=>depths(b).every(d=>d>depths(roads.find(b=>b.kind===1))[0])));
});
