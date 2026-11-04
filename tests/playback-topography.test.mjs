import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import {
  prepareTopography,
  drawTopography,
  drawContourLabels,
  contourLabelLayout,
  contourIntervalForScale,
  contourStyle,
} from "../dist/playback-topography.mjs";
globalThis.Path2D = class {
  moveTo() {}
  lineTo() {}
  closePath() {}
  addPath() {}
};
const asset = JSON.parse(
  fs.readFileSync(new URL("../dist/maps/arland-detail.json", import.meta.url)),
);
test("installed Arland detail has expected geometry and north-up building anchor", () => {
  const f = prepareTopography(asset);
  assert.equal(f.length, 14265);
  const building = f.find((x) => x.layer === "buildings");
  assert.ok(
    Math.abs((building.bounds[0] + building.bounds[2]) / 2 - 2257.6465) < 0.02,
  );
  assert.ok(
    Math.abs((building.bounds[1] + building.bounds[3]) / 2 - 1570.4696) < 0.02,
  );
  assert.deepEqual(asset.counts, {
    buildings: 348,
    contours: 151,
    powerlines: 32,
    roads: 5892,
    walls: 7322,
    water: 520,
  });
  assert.deepEqual(asset.source.waterBodies, { 2: 3, 3: 11 });
});

test("native water triangles share one fill beneath roads and respect their switch", () => {
  const features = prepareTopography({
    version: 1,
    features: [
      { layer: "roads", kind: 0, points: [0, 0, 10, 0, 10, 10] },
      { layer: "water", kind: 2, points: [0, 0, 10, 0, 10, 10] },
      { layer: "water", kind: 3, points: [0, 0, 10, 10, 0, 10] },
    ],
  });
  const colors = [];
  const ctx = {
    save() {},
    restore() {},
    translate() {},
    scale() {},
    fill() {
      colors.push(this.fillStyle);
    },
    stroke() {},
    setLineDash() {},
  };
  drawTopography(
    ctx,
    features,
    { x: 0, z: 0, scale: 1 },
    100,
    100,
    new Set(["roads", "water"]),
  );
  assert.deepEqual(colors, [
    "rgb(46.2% 88.8% 98.2%)",
    "rgb(78.4% 78.4% 78.4%)",
  ]);
  colors.length = 0;
  drawTopography(
    ctx,
    features,
    { x: 0, z: 0, scale: 1 },
    100,
    100,
    new Set(["roads"]),
  );
  assert.equal(colors.length, 1);
});
test("invalid coordinates and format fail explicitly", () => {
  for (const data of [
    { version: 2, features: [] },
    {
      version: 1,
      features: [{ layer: "roads", kind: 0, points: [1, NaN, 2, 3] }],
    },
    {
      version: 1,
      features: [{ layer: "unknown", kind: 0, points: [1, 2, 3, 4] }],
    },
  ])
    assert.throws(() => prepareTopography(data));
});
test("layer switches and viewport culling control drawing", () => {
  const features = prepareTopography({
    version: 1,
    features: [
      { layer: "buildings", kind: 0, points: [0, 0, 10, 0, 10, 10] },
      { layer: "roads", kind: 0, points: [1000, 1000, 1010, 1000, 1010, 1010] },
    ],
  });
  let fills = 0;
  const ctx = {
    save() {},
    restore() {},
    translate() {},
    scale() {},
    fill() {
      fills++;
    },
    stroke() {},
    setLineDash() {},
  };
  drawTopography(
    ctx,
    features,
    { x: 0, z: 0, scale: 1 },
    100,
    100,
    new Set(["roads"]),
  );
  assert.equal(fills, 0);
  drawTopography(
    ctx,
    features,
    { x: 0, z: 0, scale: 1 },
    100,
    100,
    new Set(["buildings"]),
  );
  assert.equal(fills, 1);
});

test("ocean-only raster preserves enabled inland geometry until depth image loads", () => {
  const features = prepareTopography({version:1,features:[
    {layer:"water",kind:2,points:[0,0,10,0,10,10]}
  ]});
  let fills=0, images=0;
  const ctx={save(){},restore(){},translate(){},scale(){},drawImage(){images++},fill(){fills++}};
  const raster={image:{},bounds:[0,0,4096,4096],includesInland:false};
  drawTopography(ctx,features,{x:0,z:0,scale:1},100,100,new Set(["water"]),raster);
  assert.equal(images,1); assert.equal(fills,1);
  raster.includesInland=true; fills=images=0;
  drawTopography(ctx,features,{x:0,z:0,scale:1},100,100,new Set(["water"]),raster);
  assert.equal(images,1); assert.equal(fills,0);
  raster.includesInland=false; fills=images=0;
  drawTopography(ctx,features,{x:0,z:0,scale:1},100,100,new Set(),raster);
  assert.equal(images,1); assert.equal(fills,0);
});

test('power lines use native voltage widths, solid strokes and ordered zoom visibility', () => {
  const features=prepareTopography({version:1,features:[0,1,2].map(kind=>({layer:'powerlines',kind,points:[-10,0,10,0]}))});
  const strokes=[];
  const ctx={save(){},restore(){},translate(){},scale(){},setLineDash(value){assert.deepEqual(value,[])},stroke(){strokes.push({color:this.strokeStyle,width:this.lineWidth})}};
  drawTopography(ctx,features,{x:0,z:0,scale:1},100,100,new Set(['powerlines']));
  assert.deepEqual(strokes.map(s=>s.width),[2.2,1.9,1.6]);
  assert.ok(strokes.every(s=>s.color==='rgb(8.2% 8.2% 8.2%)'));
  strokes.length=0;
  drawTopography(ctx,features,{x:0,z:0,scale:.15},100,100,new Set(['powerlines']));
  assert.equal(strokes.length,2);
  strokes.length=0;
  drawTopography(ctx,features,{x:0,z:0,scale:1},100,100,new Set());
  assert.equal(strokes.length,0);
});

test('contour intervals increase detail with zoom and keep every fifth line heavier',()=>{
  assert.deepEqual([.15,.5,2].map(contourIntervalForScale),[20,10,5]);
  const features=prepareTopography({version:1,features:[5,10,20,25,50,100].map(kind=>({layer:'contours',kind,points:[-200,0,200,0]}))});
  const strokes=[];const ctx={save(){},restore(){},translate(){},scale(){},stroke(){strokes.push(this.lineWidth)}};
  for(const interval of [5,10,20]){
    strokes.length=0;drawTopography(ctx,features,{x:0,z:0,scale:1},500,500,new Set(['contours']),null,interval);
    const selected=features.filter(f=>f.kind%interval===0);
    assert.deepEqual(strokes,selected.map(f=>f.kind%(interval*5)===0?.95:.55));
  }
});
test('index labels show measured metres and suppress colliding labels',()=>{
  const features=prepareTopography({version:1,features:[25,50,50].map(kind=>({layer:'contours',kind,points:[-200,0,200,0]}))});
  const labels=[];const ctx={save(){},restore(){},translate(){},rotate(){},measureText(){return {width:18}},strokeText(){},fillText(text){labels.push(text)}};
  drawContourLabels(ctx,features,{x:0,z:0,scale:1},500,500,10);
  assert.deepEqual(labels,['50']);
  assert.throws(()=>drawContourLabels(ctx,features,{x:0,z:0,scale:1},500,500,0));
});

test('textured contours fade away at overview zoom and delay labels',()=>{
  assert.equal(contourStyle(.3,true).visible,false);
  assert.equal(contourStyle(1,true).labels,false);
  assert.equal(contourStyle(2,true).labels,true);
  assert.equal(contourStyle(.3,false).visible,true);
  assert.ok(contourStyle(2,true).indexWidth < 1);
});

test('contour label collisions are shared across tile boundaries',()=>{
 const features=[{layer:'contours',kind:25,points:[40,250,1040,250]},
   {layer:'contours',kind:25,points:[152,250,1152,250]}];
 const ctx={save(){},restore(){}};
 const layout=contourLabelLayout(ctx,features,1,5);
 assert.equal(layout.length,1);assert.equal(layout[0].x,400);
 // The label centered at512 is rejected globally, rather than accepted by only
 // the tile on its right and displayed as a half-label.
 assert.ok(!layout.some(p=>p.x===512));
});
