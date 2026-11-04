import {prepareTopography, drawTopography, contourLabelLayout, contourIntervalForScale, contourStyle} from './playback-topography.mjs';
import {terrainIndex} from './playback-terrain-index.mjs';
import {drawStaticMap} from './playback-terrain.mjs';

let scene, generation, query;
const labelLayouts=new Map();
const canvas = new OffscreenCanvas(1,1);
function render(view,width,height,dpr) {
  const w=Math.ceil(width*dpr), h=Math.ceil(height*dpr);
  if (canvas.width !== w || canvas.height !== h) { canvas.width=w; canvas.height=h; }
  // Small tiles bound each render and transfer; the browser owns backing choice.
  // Keep background contour rasterization off the interactive GPU queue.
  // The worker produces CPU-backed tiles for a single upload by the compositor.
  const ctx=canvas.getContext('2d',{willReadFrequently:true});
  // Cull once for all passes, including contour labels. Keep original order so
  // water/road batches and label collision priorities remain unchanged.
  const margin=64/view.scale;
  const left=view.x-width/(2*view.scale)-margin, right=view.x+width/(2*view.scale)+margin;
  const bottom=view.z-height/(2*view.scale)-margin, top=view.z+height/(2*view.scale)+margin;
  const topography=query?.([left,bottom,right,top]);
  let contourLabels=[];
  if(scene.options.terrainContours && contourStyle(view.scale,Boolean(scene.options.terrainRelief&&scene.naturalImages.relief)).labels) {
    if(!labelLayouts.has(view.scale)) {
      labelLayouts.set(view.scale,contourLabelLayout(ctx,scene.topography??[],view.scale,contourIntervalForScale(view.scale)));
      if(labelLayouts.size>4)labelLayouts.delete(labelLayouts.keys().next().value);
    }
    contourLabels=labelLayouts.get(view.scale);
  }
  ctx.setTransform(dpr,0,0,dpr,0,0);
  const direct=scene.options.directRasters;
  if(direct)ctx.clearRect(0,0,width,height);
  else {ctx.fillStyle='#192017'; ctx.fillRect(0,0,width,height);}
  // Native imagery is composed once on the GPU, never downsampled into an overview
  // and enlarged again. Only contours need asynchronous raster tiles in this mode.
  const surface=direct?{mapConfig:null,mapImage:null,naturalImages:{relief:scene.naturalImages.relief}}:{};
  drawStaticMap(ctx,view,width,height,{...scene,...surface,topography,contourLabels,options:{...scene.options,
    ...(direct?{terrainWater:scene.options.terrainWater&&!scene.naturalImages.inlandWater}:{}),terrainTrees:false,terrainBushes:false,terrainRocks:false,terrainPower:false,sceneryOpacity:100}});
  const image=canvas.transferToImageBitmap();
  ctx.setTransform(dpr,0,0,dpr,0,0);
  ctx.clearRect(0,0,width,height);
  if (topography && scene.options.terrainPower!==false) drawTopography(ctx,topography,view,width,height,new Set(['powerlines']));
  const power=scene.options.terrainPower!==false && topography?.some(f=>f.layer==='powerlines') ? canvas.transferToImageBitmap() : null;
  return {image,power};
}
self.onmessage = ({data}) => {
  try {
    if (data.type === 'scene') {
      if (scene) {
        scene.mapImage?.close();
        Object.values(scene.naturalImages).forEach(image=>image.close());
      }
      generation=data.generation;labelLayouts.clear();
      const {base,...naturalImages}=data.images;
      scene={mapConfig:data.config,mapImage:base,naturalImages,options:data.options,
        topography:data.data ? prepareTopography(data.data) : null};
      query=scene.topography ? terrainIndex(scene.topography) : null;
      overview();
    } else if (data.type === 'options' && scene) {
      generation=data.generation;
      scene.options=data.options;
      overview();
    } else if (data.type === 'render' && data.generation === generation) {
      const gutter=32;
      const frames=render(data.view,data.width+2*gutter/data.dpr,data.height+2*gutter/data.dpr,data.dpr);
      self.postMessage({...data,type:'tile',gutter,...frames},Object.values(frames).filter(Boolean));
    }
  } catch (error) {
    self.postMessage({type:'error',generation:data.generation,id:data.id,error:String(error)});
  }
};

function overview() {
  if (scene.mapConfig) {
    const b=scene.mapConfig.bounds,scale=1536/Math.max(b[2]-b[0],b[3]-b[1]);
    const view={x:(b[0]+b[2])/2,z:(b[1]+b[3])/2,scale};
    const frames=render(view,(b[2]-b[0])*scale,(b[3]-b[1])*scale,1);
    self.postMessage({type:'overview',generation,...frames,bounds:b},Object.values(frames).filter(Boolean));
  }
  self.postMessage({type:'ready',generation});
}

