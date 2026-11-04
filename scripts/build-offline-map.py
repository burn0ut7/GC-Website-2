"""Build experimental cartography from a GC.MapExtract manifest, without an engine.

Outputs are staged under a content-derived revision. Publication is a separate
step; failure cannot replace a previously usable map. Arland is deliberately
excluded from this experiment.
"""
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import sys
import numpy as np
from PIL import Image


def module(name, file):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file))
    result=importlib.util.module_from_spec(spec);spec.loader.exec_module(result);return result


def build(manifest_path, decoded, output):
    manifest=json.loads(Path(manifest_path).read_text())
    recipe=manifest['recipe'];slug=recipe['slug']
    if slug not in ('everon','kolguyev'):
        raise ValueError('Prototype publication is restricted to Everon/Kolguyev; Arland remains unchanged')
    files={s['path']:s for s in manifest['sources']}
    topo=files[recipe['topography']]
    for entry in manifest['sources']:
        if hashlib.sha256(Path(entry['file']).read_bytes()).hexdigest()!=entry['sha256']:
            raise ValueError('Cached original changed')
    output=Path(output);output.mkdir(parents=True,exist_ok=True)
    vectors=module('native_topo','build-arland-map.py').build(topo['file'],None,recipe['bounds'],recipe['topographySha256'])
    # Authored pond/river meshes may lie under terrain. Until file-only height and
    # surface parsing is verified, retain the game's raster water, not uncut meshes.
    water_count=vectors['counts'].pop('water',0)
    vectors['features']=[f for f in vectors['features'] if f['layer']!='water']
    vectors['source']['omittedUnclippedWaterTriangles']=water_count
    (output/'details.json').write_text(json.dumps(vectors,separators=(',',':')))
    with Image.open(Path(decoded)/'raster.dds') as raster:
        raster.convert('RGB').save(output/'raster.webp',quality=90)
    columns=recipe['tileColumns'];count=recipe['tileCount']
    if count%columns or columns*256!=recipe['bounds'][2]-recipe['bounds'][0] or count//columns*256!=recipe['bounds'][3]-recipe['bounds'][1]:
        raise ValueError('Unsupported terrain tile layout/calibration')
    # Keep each 4m cell as a linear-light mean of real ground colors. This avoids
    # allocating a full 12800px RGB world and preserves a single shared seam grid.
    atlas=np.empty((count//columns*64,columns*64,3),dtype=np.float32)
    ground=module('ground_colors','build-arland-ground.py')
    for i in range(count):
        with Image.open(Path(decoded)/f'{i}.dds') as tile:
            rgb=np.asarray(ground.tile_interior(tile),dtype=np.float32)/255
        linear=np.where(rgb<=.04045,rgb/12.92,((rgb+.055)/1.055)**2.4)
        cells=linear.reshape(64,4,64,4,3).mean(axis=(1,3))
        row,col=divmod(i,columns)
        atlas[row*64:(row+1)*64,col*64:(col+1)*64]=cells
    srgb=np.where(atlas<=.0031308,atlas*12.92,1.055*np.maximum(atlas,0)**(1/2.4)-.055)
    image=Image.fromarray(np.uint8(np.rint(np.clip(srgb[::-1],0,1)*255)))
    image.save(output/'ground.webp',lossless=True)
    report=dict(version=1,name=recipe['name'],fingerprint=manifest['fingerprint'],bounds=recipe['bounds'],
                calibration=recipe['calibration'],counts=vectors['counts'],groundPixels=list(image.size),
                omitted=['terrain heights/contours','terrain-clipped water/depth','vegetation','labels/symbols','building previews'],
                sources=[dict(path=e['path'],sha256=e['sha256']) for e in manifest['sources']])
    (output/'provenance.json').write_text(json.dumps(report,indent=2))
    print(json.dumps({k:v for k,v in report.items() if k!='sources'}))


if __name__=='__main__':build(*sys.argv[1:])
