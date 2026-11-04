import {TerrainCompositor} from './playback-terrain-gpu.mjs';
import {TerrainVectors} from './playback-terrain-vectors.mjs';
// World-anchored tiles: bounded cache, one worker job, visible work before prefetch.
export class TerrainRenderer {
  constructor(changed, failed, canvas) {
    this.changed = changed;
    this.failed = failed;
    this.generation = 0;
    this.serial = 0;
    this.tiles = new Map();
    this.cacheBytes = 0;
    this.cacheBudget = 256 * 1024 * 1024;
    this.cacheLimit = this.cacheBudget;
    this.visible = new Set();
    this.queue = [];
    this.wanted = new Set();
    if(canvas) {
      const background=document.createElement('canvas');
      background.className='terrain-canvas';background.setAttribute('aria-hidden','true');
      canvas.before(background);
      try {
        this.compositor=new TerrainCompositor(background,changed);
        const geometry=document.createElement('canvas'),scenery=document.createElement('canvas'),power=document.createElement('canvas');
        for(const layer of [geometry,scenery,power]){layer.className='terrain-canvas';layer.setAttribute('aria-hidden','true');canvas.before(layer);}
        this.vectors=new TerrainVectors(geometry,power,changed,failed);
        this.scenery=new TerrainCompositor(scenery,changed,true);
      }
      catch(error){this.compositorError=error;failed(error);}
    }
    if(this.compositorError)return;
    try {
      this.worker = new Worker(new URL('./playback-terrain-worker.mjs', import.meta.url), {type:'module'});
      this.worker.onmessage = ({data}) => this.receive(data);
      this.worker.onerror = e => this.fail(e.message);
    } catch (error) { this.fail(error); }
  }
  fail(error) {
    this.worker?.terminate();
    this.worker = null;
    this.ready = false;
    this.busy = null;
    this.queue = [];
    this.clearTiles();
    closeFrame(this.overview); this.overview=null;
    this.failed(error);
  }
  update(scene, view, width, height, dpr, zooming) {
    if (!this.worker || width <= 0 || height <= 0) return;
    this.scene = scene;
    this.zooming = Boolean(zooming);
    this.latestView={view:{...view},width,height,dpr};
    const options = Object.fromEntries(Object.entries(scene.options).filter(([key])=>!['terrainTrees','terrainBushes','terrainRocks','sceneryOpacity','terrainPower',...(this.vectors?['terrainRoads','terrainBuildings']:[])].includes(key)));
    if(this.vectors){options.terrainRoads=false;options.terrainBuildings=false;options.terrainPower=false;options.directRasters=true;}
    const optionsKey = JSON.stringify(options);
    const images = Object.fromEntries(Object.entries({relief:scene.naturalImages.relief, ocean:scene.naturalImages.ocean, inlandWater:scene.naturalImages.inlandWater, base:scene.mapImage})
      .filter(([,image]) => image?.complete && image.naturalWidth > 0));
    const key = JSON.stringify(Object.entries(images).map(([name,img])=>[name,img.src]));
    if (this.key !== key || this.config !== scene.mapConfig || this.data !== scene.terrainData) {
      this.key = key;
      this.optionsKey = optionsKey;
      this.config = scene.mapConfig;
      this.data = scene.terrainData;
      this.ready = false;
      this.clearTiles();
      closeFrame(this.overview);
      this.detail = this.overview = null;
      const generation = ++this.generation;
      // Transfer each source once per scene/layer change, not once per pan.
      Promise.allSettled(Object.entries(images).map(async ([name,img]) => [name,await createImageBitmap(img)]))
        .then(results => {
          const entries = results.filter(r=>r.status === 'fulfilled').map(r=>r.value);
          const failure = results.find(r=>r.status === 'rejected');
          if (failure) { entries.forEach(([,bitmap])=>bitmap.close()); throw failure.reason; }
          if (generation !== this.generation || !this.worker) {
            entries.forEach(([,bitmap])=>bitmap.close());
            return;
          }
          this.worker.postMessage({type:'scene', generation, config:scene.mapConfig,
            data:scene.terrainData, options, images:Object.fromEntries(entries)}, entries.map(e=>e[1]));
        }).catch(error=> { if (generation === this.generation) this.fail(error); });
    }
    if (this.optionsKey !== optionsKey && this.ready) {
      this.optionsKey = optionsKey;
      this.ready = false;
      this.clearTiles();
      this.worker.postMessage({type:'options',generation:++this.generation,options});
    }
    // Keep sharp geometry/raster drawing live, but do not build contour tiles
    // for transient zoom levels that will be obsolete within the gesture.
    if (!this.ready || this.zooming) return;
    this.plan(view,width,height,dpr);
    this.dispatch();
  }
  clearTiles() {
    for(const tile of this.tiles.values()) closeFrame(tile);
    this.tiles.clear(); this.cacheBytes=0; this.queue=[]; this.wanted.clear();
    this.detail=null; this.planKey=null;
  }
  plan(view,width,height,dpr) {
    // Round resolution UP, never reduce pixel density to meet a frame budget.
    // Half-octave levels reuse tiles throughout a wheel gesture.
    const level=Math.ceil(Math.log2(view.scale*dpr)*2);
    const pixelsPerMeter=2**(level/2), span=512/pixelsPerMeter;
    const left=Math.floor((view.x-width/(2*view.scale))/span);
    const right=Math.floor((view.x+width/(2*view.scale))/span);
    const bottom=Math.floor((view.z-height/(2*view.scale))/span);
    const top=Math.floor((view.z+height/(2*view.scale))/span);
    // Keep an ahead-of-motion working set instead of waiting for exposed tiles.
    // Direction is sticky while stationary; it changes only after meaningful movement.
    if(this.lastView && this.lastView.scale===view.scale) {
      if(Math.abs(view.x-this.lastView.x)*view.scale>2)this.directionX=Math.sign(view.x-this.lastView.x);
      if(Math.abs(view.z-this.lastView.z)*view.scale>2)this.directionZ=Math.sign(view.z-this.lastView.z);
    } else {this.directionX=0;this.directionZ=0;}
    this.lastView={...view};
    const dx=this.directionX||0,dz=this.directionZ||0;
    const aheadX=Math.min(6,Math.ceil(width/(view.scale*span)));
    const aheadZ=Math.min(6,Math.ceil(height/(view.scale*span)));
    const signature=[level,dpr,left,right,bottom,top,dx,dz,this.generation].join(':');
    if(signature===this.planKey) return;
    this.planKey=signature;
    this.level=level;
    this.queue=[]; this.wanted=new Set(); this.visible=new Set();
    const candidates=[],b=this.config?.bounds;
    const predictedX=view.x+dx*width/view.scale*.75,predictedZ=view.z+dz*height/view.scale*.75;
    for(let x=left-2-(dx<0?aheadX:0);x<=right+2+(dx>0?aheadX:0);x++)
      for(let z=bottom-2-(dz<0?aheadZ:0);z<=top+2+(dz>0?aheadZ:0);z++) {
      const bounds=[x*span,z*span,(x+1)*span,(z+1)*span];
      if(b && (bounds[2]<b[0]||bounds[0]>b[2]||bounds[3]<b[1]||bounds[1]>b[3])) continue;
      const key=`${level}:${dpr}:${x}:${z}`;
      const outside=x<left||x>right||z<bottom||z>top;
      if(!outside)this.visible.add(key);
      const distance=(x*span-(outside?predictedX:view.x))**2+(z*span-(outside?predictedZ:view.z))**2;
      candidates.push({key,level,bounds,dpr,generation:this.generation,
        view:{x:(x+.5)*span,z:(z+.5)*span,scale:pixelsPerMeter/dpr},
        width:512/dpr,height:512/dpr,
        priority:(outside?1e12:0)+distance});
    }
    const tileBytes=576*576*4*2;
    this.cacheLimit=Math.max(this.cacheBudget,this.visible.size*tileBytes);
    candidates.sort((a,b)=>a.priority-b.priority);
    // Bound the planned working set too: prefetch cannot churn the cache forever.
    for(const tile of candidates.slice(0,Math.floor(this.cacheLimit/tileBytes))) {
      this.wanted.add(tile.key);
      const cached=this.tiles.get(tile.key);
      if(cached) {this.tiles.delete(tile.key);this.tiles.set(tile.key,cached);}
      else this.queue.push(tile);
    }
  }
  dispatch() {
    if (!this.worker || !this.ready || this.busy || this.zooming) return;
    let tile;
    while(this.queue.length && !tile) {
      const candidate=this.queue.shift();
      if(!this.tiles.has(candidate.key)) {
        // Completed tiles evict cold entries. A full cache must not disable prefetch.
        tile=candidate;
      }
    }
    if(!tile) return;
    const request={...tile,id:++this.serial,type:'render'};
    this.busy=request.id;
    this.worker.postMessage(request);
  }
  receive(data) {
    if (data.type === 'error') {
      if (data.id === this.busy) this.busy = null;
      if (data.generation === this.generation) this.fail(data.error);
      else this.dispatch();
      return;
    }
    if (data.type === 'tile' && data.id === this.busy) this.busy = null;
    if (data.generation !== this.generation) {
      closeFrame(data);
      this.dispatch();
      return;
    }
    if (data.type === 'overview') {
      closeFrame(this.overview);
      this.overview = data;
    } else if (data.type === 'ready') {
      this.ready = true;
      this.planKey=null;
      if(this.latestView && !this.zooming) {const {view,width,height,dpr}=this.latestView;this.plan(view,width,height,dpr);}
    } else if (data.type === 'tile') {
      const old=this.tiles.get(data.key);
      if(old) {this.cacheBytes-=old.bytes;closeFrame(old);this.tiles.delete(data.key);}
      data.bytes=(data.image.width*data.image.height+(data.power?.width??0)*(data.power?.height??0))*4;
      this.tiles.set(data.key,data);this.cacheBytes+=data.bytes;
      this.detail=data;
      // Evict outside the requested area first, then enforce the hard cap.
      for(const [key,tile] of this.tiles) {
        if(this.cacheBytes<=this.cacheLimit) break;
        if(this.wanted.has(key)) continue;
        this.tiles.delete(key);this.cacheBytes-=tile.bytes;closeFrame(tile);
      }
      for(const [key,tile] of this.tiles) {
        if(this.cacheBytes<=this.cacheLimit) break;
        if(this.visible.has(key))continue;
        this.tiles.delete(key);this.cacheBytes-=tile.bytes;closeFrame(tile);
      }
    }
    // The queue is refreshed by every view update. Keep the worker busy without
    // paying one display-frame delay per tile; only painting waits for animation.
    this.dispatch();
    this.changed();
  }
  paint(ctx, view, width, height) {
    if(this.compositorError) return;
    const overlay=ctx,dpr=ctx.getTransform().a;
    if(this.compositor) {
      const images=new Set(Object.values(this.scene?.naturalImages??{}));
      if(this.scene?.mapImage)images.add(this.scene.mapImage);
      for(const frame of [this.overview,...this.tiles.values()]) {
        if(frame?.image)images.add(frame.image);if(frame?.power)images.add(frame.power);
      }
      if(!this.compositor.begin(width,height,ctx.getTransform().a,images))return;
      ctx=this.compositor;
    }
    ctx.fillStyle = '#192017';
    ctx.fillRect(0,0,width,height);
    const scene=this.scene, bounds=scene?.mapConfig?.bounds;
    const raster=image=>{
      if(!bounds||!image?.complete||!image.naturalWidth)return;
      const [x,z,r,t]=bounds;
      paintVisible(ctx,image,width/2+(x-view.x)*view.scale,height/2-(t-view.z)*view.scale,
        (r-x)*view.scale,(t-z)*view.scale,width,height);
    };
    if(this.vectors) {
      const natural=scene.options.terrainRelief&&scene.naturalImages.ocean&&scene.naturalImages.relief;
      ctx.globalAlpha=natural?1:.7;raster(natural||scene.mapImage);ctx.globalAlpha=1;
    }
    this.paintFrames(ctx,view,width,height,'image');
    if(this.vectors&&scene.topography)raster(scene.options.terrainWater&&scene.naturalImages.inlandWater||scene.naturalImages.ocean);
    if(this.vectors) {
      this.vectors.update(this.scene?.terrainData,view,width,height,dpr,this.scene?.options??{},this.zooming);
      if(!this.scenery.begin(width,height,dpr,new Set(Object.values(this.scene?.naturalImages??{}))))return;
      this.scenery.fillRect();ctx=this.scenery;
    }
    // Loaded raster overlays compose directly: no worker message, image transfer,
    // topology rebuild, or cache invalidation for visibility/opacity controls.
    if (bounds) {
      const [minX,minZ,maxX,maxZ]=bounds;
      ctx.save();
      ctx.globalAlpha = (scene.options.sceneryOpacity ?? 100)/100;
      for (const [layer,control] of [['trees','terrainTrees'],['bushes','terrainBushes'],['rocks','terrainRocks']]) {
        const image=scene.naturalImages[layer];
        if (scene.options[control] && image?.complete && image.naturalWidth)
          paintVisible(ctx,image,width/2+(minX-view.x)*view.scale,height/2-(maxZ-view.z)*view.scale,
            (maxX-minX)*view.scale,(maxZ-minZ)*view.scale,width,height);
      }
      ctx.restore();
    }
    if(this.vectors)this.vectors.paintPower(overlay,view,width,height,scene.options);
    else if (scene?.options.terrainPower) this.paintFrames(ctx,view,width,height,'power');
  }
  paintFrames(ctx,view,width,height,layer) {
    // Disjoint rectangles avoid hundreds of cumulative GPU clip masks. Every
    // output pixel has one owner, including transparent power-line coverage.
    const tiles=[...this.tiles.values()].filter(t=>t.generation===this.generation)
      .sort((a,b)=>{
        // Prefer sufficient detail over a nearer-but-coarser zoom level.
        const rank=t=>t.level>=this.level?t.level-this.level:1000+this.level-t.level;
        return rank(a)-rank(b);
      });
    let holes=[[view.x-width/(2*view.scale),view.z-height/(2*view.scale),
      view.x+width/(2*view.scale),view.z+height/(2*view.scale)]];
    const ratio=ctx.getTransform().a;
    const paint=(image,b,source) => {
      if(!image) return;
      const x=Math.round((width/2+(b[0]-view.x)*view.scale)*ratio)/ratio;
      const y=Math.round((height/2-(b[3]-view.z)*view.scale)*ratio)/ratio;
      const r=Math.round((width/2+(b[2]-view.x)*view.scale)*ratio)/ratio;
      const bottom=Math.round((height/2-(b[1]-view.z)*view.scale)*ratio)/ratio;
      const iw=image.width,ih=image.height,sw=source[2]-source[0],sh=source[3]-source[1];
      ctx.drawImage(image,(b[0]-source[0])/sw*iw,(source[3]-b[3])/sh*ih,
        (b[2]-b[0])/sw*iw,(b[3]-b[1])/sh*ih,x,y,r-x,bottom-y);
    };
    for(const tile of tiles) {
      if(!holes.length) break;
      const b=tile.bounds, next=[];
      const pad=tile.gutter/(tile.view.scale*tile.dpr);
      const source=[b[0]-pad,b[1]-pad,b[2]+pad,b[3]+pad];
      for(const hole of holes) {
        const x=Math.max(hole[0],b[0]),z=Math.max(hole[1],b[1]);
        const r=Math.min(hole[2],b[2]),t=Math.min(hole[3],b[3]);
        if(r<=x||t<=z) {next.push(hole);continue;}
        paint(tile[layer],[x,z,r,t],source);
        if(hole[0]<x) next.push([hole[0],hole[1],x,hole[3]]);
        if(r<hole[2]) next.push([r,hole[1],hole[2],hole[3]]);
        if(hole[1]<z) next.push([x,hole[1],r,z]);
        if(t<hole[3]) next.push([x,t,r,hole[3]]);
      }
      holes=next;
    }
    if(this.overview?.[layer]) for(const hole of holes) paint(this.overview[layer],hole,this.overview.bounds);
  }

}

function closeFrame(frame) { frame?.image?.close(); frame?.power?.close(); }

function paintVisible(ctx,image,x,y,w,h,width,height) {
  const left=Math.max(0,x), top=Math.max(0,y), right=Math.min(width,x+w), bottom=Math.min(height,y+h);
  if (right <= left || bottom <= top) return;
  ctx.drawImage(image,(left-x)/w*(image.naturalWidth || image.width),(top-y)/h*(image.naturalHeight || image.height),
    (right-left)/w*(image.naturalWidth || image.width),(bottom-top)/h*(image.naturalHeight || image.height),left,top,right-left,bottom-top);
}
