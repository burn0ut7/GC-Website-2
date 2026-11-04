import test from 'node:test';
import assert from 'node:assert/strict';

test('direct raster worker retains inland polygons until the inland image is available', async () => {
  const fills=[],messages=[];
  const ctx={setTransform(){},clearRect(){},save(){},restore(){},translate(){},scale(){},setLineDash(){},stroke(){},fill(){fills.push(this.fillStyle);}};
  globalThis.Path2D=class {moveTo(){} lineTo(){} closePath(){} addPath(){}};
  globalThis.OffscreenCanvas=class {constructor(w,h){this.width=w;this.height=h;}getContext(){return ctx;}transferToImageBitmap(){return {width:this.width,height:this.height,close(){}};}};
  globalThis.self={postMessage(m){messages.push(m);}};
  await import('../dist/playback-terrain-worker.mjs');
  const scene={type:'scene',generation:1,config:{bounds:[0,0,100,100]},images:{ocean:{close(){}}},options:{directRasters:true,terrainWater:true},data:{version:1,features:[{layer:'water',kind:2,points:[0,0,10,0,10,10]}]}};
  self.onmessage({data:scene});
  assert.ok(!messages.some(m=>m.type==='error'),JSON.stringify(messages));
  assert.ok(fills.length>0,'Ocean-only imagery must not suppress inland water geometry');
  fills.length=0;
  self.onmessage({data:{...scene,generation:2,images:{ocean:{close(){}},inlandWater:{close(){}}}}});
  assert.equal(fills.length,0,'Combined depth imagery owns inland water once loaded');
  assert.ok(!messages.some(m=>m.type==='error'),JSON.stringify(messages));
});
