"""Generate north-up relief and translucent scenery layers from live Arland exports.
No textures or code from the reference mod are used. See docs/topographical-map.md.
"""
import csv, hashlib, json, sys
from pathlib import Path
import numpy as np
from PIL import Image
import importlib.util
_spec = importlib.util.spec_from_file_location('map_water', Path(__file__).with_name('map-water.py'))
_water = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_water)

SIZE = 4096
EXTENT = 4096

def relief(grid, ground_path):
    h = np.asarray(grid['heights'], dtype=np.float32).reshape(grid['rows'], grid['columns'])
    if h.shape != (int(EXTENT/8)+1, int(EXTENT/8)+1) or grid['spacing'] != 8 or not np.isfinite(h).all():
        raise ValueError('Expected finite Arland 8m height grid')
    ground = Image.open(ground_path).convert('RGB')
    if ground.size != (SIZE,SIZE):
        raise ValueError('Expected calibrated 4096px source color maps')
    dz, dx = np.gradient(h, 8)
    # Neutral relief: flat ground retains its source color, slope changes brightness only.
    light = (-dx * -.45 - dz * .55 + .70) / np.sqrt(1+dx*dx+dz*dz)
    factor = np.clip(1 + .25*(light-.70), .65, 1.10)
    factor = np.asarray(Image.fromarray(factor[::-1].astype(np.float32)).resize((SIZE,SIZE),Image.Resampling.BILINEAR))
    rgb = np.asarray(ground,dtype=np.float32)*factor[...,None]
    # Water is now a single independent layer, so river mouths have no baked seam.
    return Image.fromarray(np.uint8(np.clip(rgb,0,255)))

def group(kind):
    if kind == 100: return 'rocks'
    if kind in (1,7,8,9): return 'bushes'
    return 'trees'

def water_visibility(row):
    bottom,top,depth=[float(row[k]) for k in ('bottom','top','submergedDepth')]
    if not np.isfinite([bottom,top,depth]).all() or top<bottom or depth<0:
        raise ValueError('Invalid scenery height or water depth')
    # A top-down footprint represents the highest visible part. Emergent crowns
    # retain their weight; submerged tops fade continuously over a 2m depth scale.
    return float(np.exp(-depth/2))

def stamp(tau, x, z, rx, rz, opacity):
    # Pixel centres on a 1m grid. Bounds-derived ellipses approximate canopy/rock footprints.
    scale = SIZE/EXTENT
    cx, cy = x*scale, SIZE-z*scale
    rx, rz = rx*scale, rz*scale
    x0,x1=max(0,int(np.floor(cx-rx))),min(SIZE,int(np.ceil(cx+rx)))
    y0,y1=max(0,int(np.floor(cy-rz))),min(SIZE,int(np.ceil(cy+rz)))
    if x0>=x1 or y0>=y1: return
    xx=(np.arange(x0,x1)+.5-cx)/rx
    yy=(np.arange(y0,y1)+.5-cy)/rz
    d=yy[:,None]**2+xx[None,:]**2
    alpha=opacity*np.clip((1-d)*2,0,1)
    tau[y0:y1,x0:x1] += -np.log1p(-alpha)

def build(height_path, scenery_path, output, ground_path, slug="arland", extent=4096):
    global EXTENT
    EXTENT = extent
    out=Path(output);out.mkdir(parents=True,exist_ok=True)
    heights=Path(height_path).read_bytes();scenery=Path(scenery_path).read_bytes()
    relief(json.loads(heights), ground_path).save(out/f'{slug}-relief.webp',lossless=True)
    rows=list(csv.DictReader(scenery.decode('utf-8-sig').splitlines(),delimiter='\t'))
    counts={}
    for layer,color,opacity in [('trees',(39,91,37),.30),('bushes',(75,112,44),.25),('rocks',(63,65,63),.28)]:
        tau=np.zeros((SIZE,SIZE),dtype=np.float32);count=0
        for row in rows:
            if group(int(row['kind']))!=layer: continue
            x,z,rx,rz=[float(row[k]) for k in ('x','z','rx','rz')]
            if not np.isfinite([x,z,rx,rz]).all() or not(0<=x<=EXTENT and 0<=z<=EXTENT and 0<rx<512 and 0<rz<512):
                raise ValueError('Invalid scenery bounds')
            stamp(tau,x,z,rx,rz,opacity*water_visibility(row));count+=1
        rgba=np.empty((SIZE,SIZE,4),dtype=np.uint8);rgba[:,:,:3]=color
        rgba[:,:,3]=np.rint(255*(1-np.exp(-tau))).astype(np.uint8)
        Image.fromarray(rgba).save(out/f'{slug}-{layer}.webp',lossless=True)
        counts[layer]=count
    manifest=dict(bounds=[0,0,EXTENT,EXTENT],pixels=SIZE,heightSpacing=8,counts=counts,
        heightsSha256=hashlib.sha256(heights).hexdigest(),scenerySha256=hashlib.sha256(scenery).hexdigest(),
        footprint='world-AABB ellipse; alpha overlap; static snapshot; submerged tops fade exponentially over 2m using live water height',ground='installed terrain supertexture; repeated 4px borders removed; 4m linear-light color averages; neutral hillshade; lossless WebP',
        waterDepthStops=_water.DEPTH_STOPS,waterColors=_water.WATER_COLORS,
        groundSha256=hashlib.sha256(Path(ground_path).read_bytes()).hexdigest())
    (out/f'{slug}-natural-source.json').write_text(json.dumps(manifest,indent=2))
    print(json.dumps(manifest))

if __name__=='__main__': build(*sys.argv[1:])
