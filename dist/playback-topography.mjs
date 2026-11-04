const layers = new Set([
  "water",
  "roads",
  "buildings",
  "walls",
  "contours",
  "powerlines",
]);

// Base-game MapPropsDefault.conf (1.8.0.13), indexed by RoadType.
// Keep the native RGB values rather than applying the website's tan palette.
const roadColors = [
  "rgb(78.4% 78.4% 78.4%)", // runway
  "rgb(91.3% 83.3% 12.4%)", // main road
  "rgb(91.3% 87.3% 42.2%)", // minor road
  "rgb(57.8% 42.1% 26.8%)", // trail
  "rgb(100% 7% 7%)", // tourist path
  "rgb(15.9% 15.9% 15.9%)", // general path
  "#ffffff", // bridge: SCR_RoadTypeProps default
];

export function contourIntervalForScale(scale) {
  return scale >= 1.2 ? 5 : scale >= 0.35 ? 10 : 20;
}

// Brown index/intermediate hierarchy follows topographic-map conventions.
// Imagery uses a subdued, zoom-dependent treatment rather than white overlays.
export function contourStyle(scale, textured = false) {
  const t = Math.max(0, Math.min(1, (scale - .45) / 1.15));
  const fade = textured ? t*t*(3-2*t) : 1;
  return {
    indexColor: `rgba(165, 135, 83, ${fade * (textured ? .575 : .85)})`,
    minorColor: `rgba(155, 125, 78, ${fade * (textured ? .299 : .55)})`,
    indexWidth: textured ? .9 : .95,
    minorWidth: textured ? .5 : .55,
    visible: fade > 0,
    labels: scale >= (textured ? 1.6 : .8),
    labelColor: textured ? 'rgba(185, 157, 108, .65)' : '#a58a5d',
    labelHalo: textured ? 'rgba(40, 45, 32, .3)' : 'rgba(30, 35, 27, .65)',
  };
}

export function prepareTopography(data) {
  if (
    data.version !== 1 ||
    !Array.isArray(data.features) ||
    data.features.length > 200000
  )
    throw new Error("Unsupported map detail");
  return data.features
    .map((f) => {
      if (
        !layers.has(f.layer) ||
        !Number.isFinite(f.kind) ||
        !Array.isArray(f.points) ||
        f.points.length < 4 ||
        f.points.length % 2 ||
        f.points.length > 200000 ||
        f.points.some((v) => !Number.isFinite(v) || Math.abs(v) > 100000)
      )
        throw new Error("Invalid map feature");
      const path = new Path2D(),
        b = [Infinity, Infinity, -Infinity, -Infinity];
      for (let i = 0; i < f.points.length; i += 2) {
        const x = f.points[i],
          z = f.points[i + 1];
        if (i) path.lineTo(x, z);
        else path.moveTo(x, z);
        b[0] = Math.min(b[0], x);
        b[1] = Math.min(b[1], z);
        b[2] = Math.max(b[2], x);
        b[3] = Math.max(b[3], z);
      }
      if (["water", "roads", "buildings", "walls"].includes(f.layer))
        path.closePath();
      return { layer: f.layer, kind: f.kind, path, bounds: b,
        model: f.model, models: f.models, ambiguous: f.ambiguous === true,
        points: ["contours", "buildings", "walls"].includes(f.layer) ? f.points : null };
    })
    .sort(
      (a, b) =>
        [
          "contours",
          "water",
          "roads",
          "walls",
          "buildings",
          "powerlines",
        ].indexOf(a.layer) -
        [
          "contours",
          "water",
          "roads",
          "walls",
          "buildings",
          "powerlines",
        ].indexOf(b.layer),
    );
}

// Called only when view, size, or layer switches change, not on each replay frame.
export function drawTopography(
  ctx,
  features,
  view,
  width,
  height,
  enabled,
  waterRaster = null,
  contourInterval = 10,
  style = contourStyle(view.scale),
) {
  if (![5, 10, 20].includes(contourInterval)) throw new Error("Invalid contour interval");
  const left = view.x - width / (2 * view.scale),
    right = view.x + width / (2 * view.scale);
  const bottom = view.z - height / (2 * view.scale),
    top = view.z + height / (2 * view.scale);
  ctx.save();
  ctx.translate(
    width / 2 - view.x * view.scale,
    height / 2 + view.z * view.scale,
  );
  ctx.scale(view.scale, -view.scale);
  // Rasterize adjoining road pieces together so shared edges are internal,
  // rather than separately antialiased against the background.
  const roadBatches = new Map();
  let waterBatch = null;
  let rasterDrawn = false;
  const drawWaterRaster = () => {
    if (!waterRaster || rasterDrawn) return;
    const { image, bounds: b } = waterRaster;
    ctx.save();
    ctx.translate(b[0], b[3]);
    ctx.scale(1, -1);
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = "high";
    ctx.drawImage(image, 0, 0, b[2] - b[0], b[3] - b[1]);
    ctx.restore();
    rasterDrawn = true;
  };
  const flushWater = () => {
    if (!waterBatch) return;
    // MapPropsDefault.conf m_LandWaterColor; all triangles in one fill avoid seams.
    ctx.fillStyle = "rgb(46.2% 88.8% 98.2%)";
    ctx.fill(waterBatch);
    waterBatch = null;
  };
  const flushRoads = () => {
    for (const [kind, path] of roadBatches) {
      ctx.fillStyle = roadColors[kind] ?? "#ffffff";
      ctx.fill(path);
    }
    roadBatches.clear();
  };
  for (const f of features) {
    if (f.layer !== "contours") drawWaterRaster();
    if (waterRaster?.includesInland && f.layer === "water") continue;
    const b = f.bounds;
    if (
      !enabled.has(f.layer) ||
      b[2] < left ||
      b[0] > right ||
      b[3] < bottom ||
      b[1] > top
    )
      continue;
    if (f.layer !== "roads" && roadBatches.size) flushRoads();
    if (f.layer !== "water") flushWater();
    if (f.layer === "contours") {
      // The continuous water coverage owns the coastline, including river mouths.
      if (!style.visible || f.kind === 0) continue;
      if (f.kind % contourInterval !== 0) continue;
      const index = f.kind % (contourInterval * 5) === 0;
      ctx.strokeStyle = index ? style.indexColor : style.minorColor;
      ctx.lineWidth = (index ? style.indexWidth : style.minorWidth) / view.scale;
      ctx.lineJoin = "round";
      ctx.stroke(f.path);
    } else if (f.layer === "water") {
      if (!waterBatch) waterBatch = new Path2D();
      waterBatch.addPath(f.path);
    } else if (f.layer === "roads") {
      if (!roadBatches.has(f.kind)) roadBatches.set(f.kind, new Path2D());
      roadBatches.get(f.kind).addPath(f.path);
    } else if (f.layer === "powerlines") {
      // MapPropsDefault.conf: very high / high / low voltage, solid dark lines.
      // Match native type visibility ordering using our continuous zoom scale.
      if (view.scale < [0.0625, 0.125, 0.25][f.kind]) continue;
      ctx.strokeStyle = "rgb(8.2% 8.2% 8.2%)";
      ctx.lineWidth = ([2.2, 1.9, 1.6][f.kind] ?? 1.6) / view.scale;
      ctx.lineJoin = "round";
      ctx.lineCap = "round";
      ctx.setLineDash([]);
      ctx.stroke(f.path);
    } else {
      if (f.layer === "walls" && view.scale < 0.4) continue;
      ctx.fillStyle =
        f.kind === 0 ? "rgb(45.6% 45.6% 45.6%)" : "rgb(20.4% 20.4% 20.4%)";
      ctx.fill(f.path);
      // Native walls have no outline; ordinary buildings have a dark gray edge.
      if (f.layer === "walls") continue;
      ctx.strokeStyle = f.kind === 0 ? "rgb(20.5% 20.5% 20.5%)" : "#000000";
      ctx.lineWidth = (f.layer === "walls" ? 0.45 : 0.9) / view.scale;
      ctx.stroke(f.path);
    }
  }
  flushRoads();
  flushWater();
  drawWaterRaster();
  ctx.restore();
}

// Elevation labels sit on index contours, with screen-space collision spacing.
// Geometry remains the measured terrain isoline; no curve smoothing moves heights.
export function contourLabelLayout(ctx, features, scale, interval) {
  const occupied=new Map(), result=[];
  ctx.save();ctx.font="600 11px system-ui";
  for(const f of features) {
    if(f.layer!=="contours"||f.kind<=0||f.kind%(interval*5))continue;
    let distance=0,next=360;
    for(let i=2;i<f.points.length;i+=2) {
      const dx=(f.points[i]-f.points[i-2])*scale,dy=(f.points[i+1]-f.points[i-1])*scale;
      const length=Math.hypot(dx,dy);
      while(length>0 && next<=distance+length) {
        const t=(next-distance)/length,x=f.points[i-2]*scale+dx*t,z=f.points[i-1]*scale+dy*t;
        next+=1280;
        const gx=Math.floor(x/180),gz=Math.floor(z/180);let collision=false;
        for(let a=gx-1;a<=gx+1;a++)for(let b=gz-1;b<=gz+1;b++)
          if((occupied.get(`${a},${b}`)??[]).some(p=>Math.hypot(p[0]-x,p[1]-z)<180))collision=true;
        if(collision)continue;
        const key=`${gx},${gz}`;if(!occupied.has(key))occupied.set(key,[]);occupied.get(key).push([x,z]);
        let angle=Math.atan2(-dy,dx);if(angle>Math.PI/2)angle-=Math.PI;if(angle< -Math.PI/2)angle+=Math.PI;
        result.push({x:x/scale,z:z/scale,angle,label:String(f.kind)});
      }
      distance+=length;
    }
  }
  ctx.restore();return result;
}
export function drawContourLabels(ctx, features, view, width, height, interval, style = contourStyle(view.scale), placements = null) {
  if (![5,10,20].includes(interval))throw new Error("Invalid contour interval");
  if(!style.visible||!style.labels)return;
  // One world-wide layout per resolution: adjacent tiles make identical collision
  // decisions and draw both halves of labels crossing their common edge.
  placements ??= contourLabelLayout(ctx,features,view.scale,interval);
  ctx.save();ctx.font="600 11px system-ui";ctx.textAlign="center";ctx.textBaseline="middle";ctx.lineJoin="round";
  for(const p of placements) {
    const x=width/2+(p.x-view.x)*view.scale,y=height/2-(p.z-view.z)*view.scale;
    if(x< -64||y< -64||x>width+64||y>height+64)continue;
    ctx.save();ctx.translate(x,y);ctx.rotate(p.angle);
    ctx.strokeStyle=style.labelHalo;ctx.lineWidth=2.5;ctx.strokeText(p.label,0,0);
    ctx.fillStyle=style.labelColor;ctx.fillText(p.label,0,0);ctx.restore();
  }
  ctx.restore();
}

export function prepareMapLabels(data) {
  if (
    data?.version !== 1 ||
    !Array.isArray(data.labels) ||
    data.labels.length > 2000
  )
    throw new Error("Unsupported place labels");
  return data.labels
    .map((label) => {
      if (
        typeof label.name !== "string" ||
        !label.name.trim() ||
        label.name.length > 160 ||
        !Number.isFinite(label.x) ||
        !Number.isFinite(label.z) ||
        !["town", "place", "landscape"].includes(label.category)
      )
        throw new Error("Invalid place label");
      return label;
    })
    .sort(
      (a, b) =>
        ["town", "place", "landscape"].indexOf(a.category) -
          ["town", "place", "landscape"].indexOf(b.category) ||
        a.name.localeCompare(b.name),
    );
}

export function drawMapLabels(ctx, labels, view, width, height) {
  const occupied = [];
  ctx.save();
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.lineJoin = "round";
  ctx.shadowBlur = 0;
  ctx.shadowOffsetX = 0;
  ctx.shadowOffsetY = 0;
  ctx.shadowColor = "transparent";
  const pixelRatio = ctx.getTransform().a || 1;
  for (const label of labels) {
    // Towns anchor the overview; smaller named places and landscape features
    // share a detail tier, about 32% closer than the previous POI threshold.
    if (view.scale < (label.category === "town" ? 0.1 : 0.29)) continue;
    const x = Math.round((width / 2 + (label.x - view.x) * view.scale) * pixelRatio) / pixelRatio;
    const y = Math.round((height / 2 - (label.z - view.z) * view.scale) * pixelRatio) / pixelRatio;
    const size = label.category === "town" ? 16 : 14;
    ctx.font = `${label.category === "town" ? "600" : "400"} ${size}px Arial, sans-serif`;
    const half = ctx.measureText(label.name).width / 2 + 5;
    const box = [x - half, y - size / 2 - 4, x + half, y + size / 2 + 4];
    if (box[2] < 0 || box[0] > width || box[3] < 0 || box[1] > height) continue;
    if (
      occupied.some(
        (b) => box[0] < b[2] && box[2] > b[0] && box[1] < b[3] && box[3] > b[1],
      )
    )
      continue;
    occupied.push(box);
    ctx.strokeStyle = "#20251f";
    ctx.lineWidth = 2;
    ctx.strokeText(label.name, x, y);
    ctx.fillStyle = "#f5f3e9";
    ctx.fillText(label.name, x, y);
  }
  ctx.restore();
}

import { mapSymbolVisible } from "./playback-symbol-visibility.mjs";

export function prepareMapSymbols(data) {
  if (
    data?.version !== 1 ||
    !Array.isArray(data.symbols) ||
    data.symbols.length > 10000 ||
    !data.styles
  )
    throw new Error("Unsupported map symbols");
  return data.symbols.map((s) => {
    const style = data.styles[s.type];
    if (
      !style ||
      !/^\/(?:map-packages\/[a-z0-9][a-z0-9-]{0,63}\/[a-f0-9]{64}\/)?icons\/reforger\/topo-MDT_[A-Z_]+\.png$/.test(style.url) ||
      ![s.x, s.z, s.yaw, style.size, style.viewLayer].every(Number.isFinite) ||
      style.size <= 0 ||
      style.size > 128 ||
      style.viewLayer < 0 ||
      style.viewLayer > 6
    )
      throw new Error("Invalid map symbol");
    return { ...s, style };
  });
}

export function drawMapSymbols(ctx, symbols, view, width, height, getImage) {
  ctx.save();
  for (const symbol of symbols) {
    const style = symbol.style;
    if (!mapSymbolVisible(style, view.scale)) continue;
    const x = width / 2 + (symbol.x - view.x) * view.scale,
      y = height / 2 - (symbol.z - view.z) * view.scale;
    const size = style.size * Math.max(0.5, Math.min(1, view.scale));
    if (x < -size || y < -size || x > width + size || y > height + size)
      continue;
    const image = getImage(style);
    if (!image) continue;
    ctx.save();
    ctx.translate(x, y);
    if (style.rotate) ctx.rotate((symbol.yaw * Math.PI) / 180);
    ctx.shadowColor = "rgba(244,240,218,.8)";
    ctx.shadowBlur = 2;
    ctx.drawImage(image, -size / 2, -size / 2, size, size);
    ctx.restore();
  }
  ctx.restore();
}
