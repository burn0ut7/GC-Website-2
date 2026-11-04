"""Prepare native-water probes or build shading from the explicit Workbench export."""
import csv, hashlib, importlib.util, json, sys
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw

_spec = importlib.util.spec_from_file_location('map_water', Path(__file__).with_name('map-water.py'))
palette = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(palette)
SIZE=4096
EXTENT=4096

def mask_for(details):
    image=Image.new('1',(SIZE,SIZE));draw=ImageDraw.Draw(image)
    for f in details['features']:
        if f['layer']=='water':
            draw.polygon([(f['points'][i]*SIZE/EXTENT-.5,SIZE-f['points'][i+1]*SIZE/EXTENT-.5) for i in range(0,len(f['points']),2)],fill=1)
    return np.asarray(image)

def probes(detail_path, output, extent=4096):
    global EXTENT
    EXTENT = extent
    ys,xs=np.nonzero(mask_for(json.loads(Path(detail_path).read_text())))
    Path(output).write_text(''.join(f'{(x+.5)*EXTENT/SIZE}\t{(SIZE-y-.5)*EXTENT/SIZE}\n' for x,y in zip(xs,ys)))
    print(f'{len(xs)} probe points')

def build(detail_path, height_path, samples_path, output, extent=4096, slug="arland", ocean_level=0):
    global EXTENT
    EXTENT = extent
    details=json.loads(Path(detail_path).read_text());mask=mask_for(details)
    h=json.loads(Path(height_path).read_text())
    if (h['rows'],h['columns'],h['spacing'])!=(int(EXTENT/8)+1,int(EXTENT/8)+1,8): raise ValueError('Unsupported terrain grid')
    heights=np.asarray(h['heights'],dtype=np.float32).reshape(h['rows'],h['columns'])
    if not np.isfinite(heights).all(): raise ValueError('Invalid terrain grid')
    sea_depth=np.zeros((SIZE,SIZE),dtype=np.float32) if ocean_level is None else ocean_level-palette.terrain_pixels(heights,SIZE)
    ocean=sea_depth>0
    depths=np.maximum(sea_depth,0);wet=ocean.copy();seen=np.zeros_like(wet)
    raw=Path(samples_path).read_bytes();counts={};measured_wet=0
    for row in csv.DictReader(raw.decode('utf-8-sig').splitlines(),delimiter='\t'):
        x,z,ground,surface=[float(row[k]) for k in ('x','z','ground','surface')]
        if not np.isfinite([x,z,ground,surface]).all() or not(0<x<EXTENT and 0<z<EXTENT):
            raise ValueError('Invalid water sample')
        ix,iy=int(x*SIZE/EXTENT),SIZE-1-int(z*SIZE/EXTENT)
        # Enforce's TSV formatting rounds five-digit coordinates to 0.1m.
        if abs(x-(ix+.5)*EXTENT/SIZE) > .051 or abs(z-(SIZE-iy-.5)*EXTENT/SIZE) > .051: raise ValueError("Off-grid sample")
        if seen[iy,ix] or not mask[iy,ix]: raise ValueError('Duplicate or unexpected sample')
        seen[iy,ix]=True
        if row['wet'] not in ('0','1'): raise ValueError('Invalid wet flag')
        kind=int(row['type'])
        if row['wet']=='0':
            wet[iy,ix]=False
            continue
        if kind not in (1,2,3): raise ValueError('Invalid water surface')
        measured_wet+=1
        if surface <= ground:
            wet[iy,ix]=False
            continue
        counts[kind]=counts.get(kind,0)+1
        wet[iy,ix]=True
        # Use the identical sea-depth field where rivers meet the ocean.
        depths[iy,ix]=max(0,sea_depth[iy,ix] if kind==1 else surface-ground)
    if not np.array_equal(seen,mask): raise ValueError('Incomplete depth export')
    def render(coverage, field, path):
        alpha=palette.smooth(coverage)
        # Normalized depth filtering prevents dry ground from darkening/lightening
        # the shore. One field crosses the river/ocean boundary before coloring.
        depth=palette.smooth(field*coverage)/np.maximum(alpha,1e-6)
        rgba=np.zeros((SIZE,SIZE,4),dtype=np.uint8)
        rgba[:,:,:3]=np.uint8(np.rint(palette.water_colors(depth)))
        rgba[:,:,3]=np.uint8(np.rint(np.clip(alpha,0,1)*255))
        Image.fromarray(rgba).save(path,lossless=True)
    render(wet,depths,output)
    render(ocean,np.maximum(sea_depth,0),Path(output).with_name(f'{slug}-ocean.webp'))
    meta=dict(samples=int(seen.sum()),measuredWetSamples=measured_wet,visibleProbeSamples=sum(counts.values()),waterPixels=int(wet.sum()),types=counts,
        heightsSha256=hashlib.sha256(Path(height_path).read_bytes()).hexdigest(),
        sourceSha256=hashlib.sha256(raw).hexdigest(),detailSha256=hashlib.sha256(Path(detail_path).read_bytes()).hexdigest(),
        depthStops=palette.DEPTH_STOPS,colors=palette.WATER_COLORS,oceanBaseHeight=ocean_level,depthRange=[float(depths[wet].min()),float(depths[wet].max())] if wet.any() else [],
        source=f'Unified coverage: live {EXTENT/SIZE:g}m inland depth samples and 8m ocean height grid; vertex-aligned bicubic terrain; {1.2*EXTENT/SIZE:g}m Gaussian coverage and normalized depth smoothing; dry banks excluded before smoothing')
    Path(output).with_suffix('.json').write_text(json.dumps(meta,indent=2));print(json.dumps(meta))

if __name__=='__main__':
    if sys.argv[1]=='probes':probes(*sys.argv[2:])
    elif sys.argv[1]=='build':build(*sys.argv[2:])
    else:raise ValueError('Expected probes or build')
