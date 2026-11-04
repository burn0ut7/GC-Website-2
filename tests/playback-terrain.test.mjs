import test from 'node:test';
import assert from 'node:assert/strict';
import {TerrainRenderer} from '../dist/playback-terrain-client.mjs';
import {terrainIndex} from '../dist/playback-terrain-index.mjs';
class WorkerStub { messages=[]; postMessage(m){this.messages.push(m);} terminate(){} }
globalThis.Worker=WorkerStub;
const state=()=>({mapConfig:{bounds:[-10000,-10000,10000,10000]},terrainData:null,naturalImages:{},options:{}});
const view={x:100,z:100,scale:1};
const flush=()=>new Promise(r=>setImmediate(r));
const bitmap=()=>({width:576,height:576,closed:false,close(){this.closed=true;}});
async function ready(dpr=1){const r=new TerrainRenderer(()=>{},assert.fail),s=state();r.update(s,view,800,600,dpr,false);await flush();r.receive({type:'ready',generation:1});r.update(s,view,800,600,dpr,false);return [r,s];}
function finish(r){const job=r.worker.messages.at(-1),image=bitmap();r.receive({...job,type:'tile',gutter:32,image,power:bitmap()});return image;}
function drain(r,s,v=view,dpr=1){for(let n=0;n<200;n++){if(r.busy)finish(r);r.update(s,v,800,600,dpr,false);if(!r.busy&&!r.queue.length)return;}assert.fail('queue did not settle');}

test('rapid zoom waits for the final view before requesting native resolution tiles',async()=>{
 const [r,s]=await ready(2);const first=r.worker.messages.at(-1);assert.ok(first.view.scale*first.dpr>=view.scale*2);
 for(const scale of [.2,.5,2,4])r.update(s,{...view,scale},800,600,2,true);
 assert.equal(r.worker.messages.filter(m=>m.type==='render').length,1);
 finish(r);
 assert.equal(r.busy,null);assert.equal(r.serial,1);
 r.update(s,{...view,scale:4},800,600,2,true);assert.equal(r.serial,1);
 r.update(s,{...view,scale:4},800,600,2,false);
 const next=r.worker.messages.at(-1);assert.ok(next.view.scale>=4);assert.equal(next.dpr,2);
 assert.equal(next.width*next.dpr,512);
});
test('panning reuses world tiles and only schedules newly exposed areas',async()=>{
 const [r,s]=await ready();drain(r,s);const count=r.serial,tiles=r.tiles.size;
 r.update(s,{...view,x:101},800,600,1,false);assert.equal(r.serial,count);assert.equal(r.tiles.size,tiles);
 r.update(s,{...view,x:700},800,600,1,false);assert.ok(r.serial>count);assert.ok(r.tiles.size>=tiles);
});
test('worker completions immediately dispatch the latest queued view without a display-frame delay',async()=>{
 const [r,s]=await ready();const first=r.worker.messages.at(-1);assert.ok(first.priority<1e12);
 r.update(s,{...view,x:5000},800,600,1,false);
 const count=r.serial;finish(r);assert.equal(r.serial,count+1);assert.ok(r.busy);
 assert.ok(r.worker.messages.at(-1).view.x>4000);
 r.update(s,{...view,x:5000},800,600,1,false);assert.equal(r.serial,count+1);
});
test('scene changes close cached and stale images and invalidate tile coordinates',async()=>{
 const [r,s]=await ready();const old=finish(r);r.update({...s,terrainData:{features:[]}},view,800,600,1,false);
 assert.ok(old.closed);assert.equal(r.tiles.size,0);
 const stale=bitmap();r.receive({type:'tile',generation:1,image:stale,id:0});assert.ok(stale.closed);
 await flush();assert.equal(r.worker.messages.at(-1).generation,2);
});
test('scenery controls retain tiles; geometry controls invalidate them',async()=>{
 const [r,s]=await ready();drain(r,s);const count=r.serial;
 r.update({...s,options:{terrainTrees:false,terrainPower:false,sceneryOpacity:50}},view,800,600,1,false);
 assert.equal(r.serial,count);assert.equal(r.generation,1);
 r.update({...s,options:{terrainRoads:false}},view,800,600,1,false);
 assert.equal(r.tiles.size,0);assert.equal(r.worker.messages.at(-1).type,'options');
});
test('bitmap cache enforces its byte budget and closes evicted buffers',async()=>{
 const [r,s]=await ready();r.cacheBudget=0;r.planKey=null;const first=finish(r);
 drain(r,s);r.update(s,{...view,x:5000},800,600,1,false);drain(r,s,{...view,x:5000});assert.ok(r.cacheBytes<=r.cacheLimit);assert.ok(first.closed);
});

test('a full cache still schedules ahead-of-pan detail and bounds its working set',async()=>{
 const [r,s]=await ready();r.cacheBudget=24*576*576*8;r.planKey=null;drain(r,s);
 r.update(s,{...view,x:1500},800,600,1,false);drain(r,s,{...view,x:1500});
 assert.ok(r.wanted.size<=24);assert.ok(r.cacheBytes<=r.cacheLimit);
 assert.ok([...r.wanted].some(key=>!r.visible.has(key)&&r.tiles.has(key)));
 const centers=[...r.wanted].map(key=>r.tiles.get(key)?.view.x).filter(Number.isFinite);
 assert.ok(Math.max(...centers)>1500+800/2);
});
test('spatial index matches a full intersection scan including long and negative geometry',()=>{
 const features=Array.from({length:200},(_,i)=>({bounds:[i*31-3000,i*17-1000,i*31-2900,i*17-800]}));
 features.push({bounds:[-10000,-10000,10000,10000]});const query=terrainIndex(features);
 for(const box of [[-100,-200,300,400],[-4000,-3000,-2900,-800],[-20000,-20000,20000,20000]]){
 const expected=features.filter(f=>f.bounds[2]>=box[0]&&f.bounds[0]<=box[2]&&f.bounds[3]>=box[1]&&f.bounds[1]<=box[3]);
 assert.deepEqual(query(box),expected);
 }
});

test('large HiDPI view retains every visible tile after all work finishes',async()=>{
 const r=new TerrainRenderer(()=>{},assert.fail),s=state(),v={x:0,z:0,scale:.71};
 r.update(s,v,3840,2160,2,false);await flush();r.receive({type:'ready',generation:1});
 for(let n=0;n<1000;n++){
   r.update(s,v,3840,2160,2,false);
   if(!r.busy&&!r.queue.length)break;
   const job=r.worker.messages.at(-1);r.receive({...job,type:'tile',gutter:32,image:bitmap(),power:bitmap()});
 }
 assert.equal(r.busy,null);assert.equal(r.queue.length,0);
 assert.ok(r.visible.size>200);
 for(const key of r.visible)assert.ok(r.tiles.has(key),`visible tile missing: ${key}`);
 assert.ok(r.cacheBytes<=r.cacheLimit);
 const count=r.serial;r.update(s,v,3840,2160,2,false);assert.equal(r.serial,count);
});

 test('scene becoming ready during a zoom waits for settle and uses the latest view',async()=>{
 const r=new TerrainRenderer(()=>{},assert.fail),s=state();
 r.update(s,view,800,600,1,true);await flush();
 r.receive({type:'ready',generation:1});assert.equal(r.serial,0);
 r.update(s,{...view,scale:8},800,600,1,true);assert.equal(r.serial,0);
 r.update(s,{...view,scale:8},800,600,1,false);
 assert.equal(r.serial,1);assert.ok(r.worker.messages.at(-1).view.scale>=8);
 });
