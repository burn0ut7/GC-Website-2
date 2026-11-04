"""Build a portable map staging folder from explicit Workbench exports.
Phases: terrain (also writes water probes and photo queue), finish (after water/photos).
The original Arland assets are never overwritten. See docs/manual-map-import.md.
"""
import argparse, csv, hashlib, importlib.util, json, math, shutil
from pathlib import Path
import numpy as np
from PIL import Image

def module(name,file):
    spec=importlib.util.spec_from_file_location(name,Path(__file__).with_name(file));value=importlib.util.module_from_spec(spec);spec.loader.exec_module(value);return value

def build(args):
    manifest=json.loads(Path(args.sources).read_text());recipe=manifest['recipe'];slug=recipe['slug']
    root=Path(args.output);profile=Path(args.profile);decoded=Path(args.decoded)
    maps=root/'dist/maps';maps.mkdir(parents=True,exist_ok=True);(root/'artifacts/map-tools').mkdir(parents=True,exist_ok=True)
    meta=json.loads((profile/f'GC_MapExport_{slug}.json').read_text());extent=recipe['bounds'][2]
    if not meta['world'].lower().endswith(recipe['worldSuffix'].lower()): raise ValueError('Wrong Workbench world')
    size=meta['size']
    if isinstance(size,str): size=[float(n) for n in size.split()]
    if size[0]!=extent or size[2]!=extent or recipe['bounds']!=[0,0,extent,extent]: raise ValueError('Calibration mismatch')
    source={s['path']:s for s in manifest['sources']};topo=source[recipe['topography']]
    heights=profile/f'GC_MapDetail_{slug}.json';detail=maps/f'{slug}-detail.json'
    if args.phase=='terrain':
        data=module('topo','build-arland-map.py').build(topo['file'],heights,recipe['bounds'],recipe['topographySha256'])
        detail.write_text(json.dumps(data,separators=(',',':')))
        print('Vectors',data['counts'],flush=True)
        module('water','build-arland-water.py').probes(detail,root/'water-probes.tsv',extent)
        # The installed tiles repeat a 4px boundary. Measured worst edge mean
        # errors: Everon .200, Kolguyev .218 of one 8-bit channel step (BC7).
        ground=module('ground','build-arland-ground.py');columns=recipe['tileColumns'];count=recipe['tileCount']
        if columns*256!=extent or count!=columns*columns: raise ValueError('Unsupported source tile grid')
        atlas=np.empty((columns*64,columns*64,3),dtype=np.float32)
        for index in range(count):
            with Image.open(decoded/f'{index}.dds') as tile: rgb=np.asarray(ground.tile_interior(tile,.25),dtype=np.float32)/255
            linear=np.where(rgb<=.04045,rgb/12.92,((rgb+.055)/1.055)**2.4)
            row,col=divmod(index,columns);atlas[row*64:(row+1)*64,col*64:(col+1)*64]=linear.reshape(64,4,64,4,3).mean(axis=(1,3))
        linear=np.stack([np.asarray(Image.fromarray(atlas[::-1,:,i]).resize((4096,4096),Image.Resampling.BILINEAR)) for i in range(3)],axis=2)
        srgb=np.where(linear<=.0031308,linear*12.92,1.055*np.maximum(linear,0)**(1/2.4)-.055)
        Image.fromarray(np.uint8(np.rint(np.clip(srgb,0,1)*255))).save(root/'ground.png')
        module('natural','build-arland-natural.py').build(heights,profile/f'GC_MapScenery_{slug}.tsv',maps,root/'ground.png',slug,extent)
        with Image.open(decoded/'raster.dds') as image: image.convert('RGB').save(maps/f'{slug}.webp',quality=90)
        labels=[]
        for row in csv.DictReader((profile/f'GC_MapLabels_{slug}.tsv').read_text(encoding='utf-8-sig').splitlines(),delimiter='\t'):
            kind=row['type'].removeprefix('MDT_NAME_');x,z=float(row['x']),float(row['z'])
            if not(0<=x<=extent and 0<=z<=extent) or not row['name'].strip() or row['name'].startswith('#'): raise ValueError('Invalid place name')
            labels.append(dict(name=row['name'],x=x,z=z,category='town' if kind in ('CITY','TOWN','VILLAGE','SETTLEMENT') else 'place' if kind=='GENERIC' else 'landscape',type=kind))
        (maps/f'{slug}-labels.json').write_text(json.dumps(dict(version=1,labels=labels)))
        # These style definitions were extracted from the installed base-game
        # map descriptor configuration, not assigned from object-name guesses.
        baseline=Path(__file__).resolve().parent.parent/'dist'
        styles=json.loads((baseline/'maps/native-symbols.json').read_text())['styles'];symbols=[];unmapped={}
        for row in csv.DictReader((profile/f'GC_MapSymbols_{slug}.tsv').read_text().splitlines(),delimiter='\t'):
            kind=row['type']
            if kind not in styles: unmapped[kind]=unmapped.get(kind,0)+1;continue
            symbols.append(dict(type=kind,x=float(row['x']),z=float(row['z']),yaw=float(row['yaw'])))
        used={kind:styles[kind] for kind in {s['type'] for s in symbols}}
        for style in used.values():
            path=style['url'].lstrip('/');target=root/'dist'/path;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(baseline/path,target)
        (maps/f'{slug}-symbols.json').write_text(json.dumps(dict(version=1,styles=used,symbols=symbols,unmapped=unmapped)))
        print(f'Place names: {len(labels)}; symbols: {len(symbols)}; unmapped symbol types: {unmapped}',flush=True)
        module('buildings','build-arland-buildings.py').build(profile,root,slug,slug)
    if args.phase=='finish':
        module('buildings','build-arland-buildings.py').finish_previews(profile,root,slug)
        ocean_file=root/'ocean.json'
        ocean_level=0  # Original base-game captures used a verified zero-metre ocean.
        if ocean_file.exists():
            ocean=json.loads(ocean_file.read_text())
            if ocean['world'] != meta['world']: raise ValueError('Ocean calibration belongs to a different world')
            ocean_level=ocean['level']
            if ocean_level is not None and not math.isfinite(ocean_level): raise ValueError('Invalid ocean height')
        elif 'workshopId' in recipe:
            raise ValueError('Capture native ocean calibration before finishing a workshop map')
        module('water','build-arland-water.py').build(detail,heights,root/'water-depth.tsv',maps/f'{slug}-water.webp',extent,slug,ocean_level)
        data=json.loads(detail.read_text());parts=[];batch=[];size=0
        def write_part():
            path=f'maps/{slug}-detail-{len(parts)}.json';parts.append('/'+path)
            (root/'dist'/path).write_text(json.dumps(dict(version=1,bounds=data['bounds'],features=batch),separators=(',',':')))
        for feature in data['features']:
            length=len(json.dumps(feature,separators=(',',':')).encode())
            if length>2_000_000: raise ValueError('Individual terrain path exceeds playback part limit')
            if batch and size+length>2_000_000: write_part();batch=[];size=0
            batch.append(feature);size+=length+1
        if batch: write_part()
        (maps/f'{slug}-detail-index.json').write_text(json.dumps(dict(version=1,parts=parts,source=data['source'])))
        config=dict(name=recipe['name'],worldSuffix=recipe['worldSuffix'],bounds=recipe['bounds'],image=f'/maps/{slug}.webp',details=f'/maps/{slug}-detail-index.json',
            labels=f'/maps/{slug}-labels.json',symbols=f'/maps/{slug}-symbols.json',buildings=f'/maps/{slug}-buildings.json',
            natural={k:f'/maps/{slug}-{v}.webp' for k,v in dict(inlandWater='water',ocean='ocean',relief='relief',trees='trees',bushes='bushes',rocks='rocks').items()},
            source='Workbench terrain, water and descriptors; installed native map geometry and ground colors',
            provenance=dict(world=meta['world'],oceanBaseHeight=ocean_level,topographySha256=recipe['topographySha256'],heightSha256=hashlib.sha256(heights.read_bytes()).hexdigest(),sourceFingerprint=manifest['fingerprint'],groundCellMetres=4,layerPixels=4096,waterProbeSpacingMetres=extent/4096))
        if recipe.get('worldAliases'):
            config['worldAliases']=recipe['worldAliases']
        if (root/'river-surfaces.tsv').exists():
            config['provenance']['riverSurfacesSha256']=hashlib.sha256((root/'river-surfaces.tsv').read_bytes()).hexdigest()
            config['provenance']['waterProbeMethod']='terrain-to-river-surface-column-0.5m'
        (root/'dist/playback-maps.json').write_text(json.dumps([config],indent=2))
        module('package','package-map.py').build(root/'dist',config['worldSuffix'],slug,root/f'{slug}.zip')

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ('sources','decoded','profile','output'): p.add_argument('--'+name,required=True)
    p.add_argument('--phase',choices=['terrain','finish'],required=True)
    build(p.parse_args())
