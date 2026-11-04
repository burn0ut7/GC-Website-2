"""Export only the game's map raster from installed PAKs; no Workbench required."""
import argparse
import hashlib
import importlib.util
import json
import math
import re
import subprocess
import sys
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent

def validate(recipe):
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,54}', recipe['slug']):
        raise ValueError('Invalid map slug')
    bounds = recipe['bounds']
    if len(bounds) != 4 or not all(math.isfinite(v) for v in bounds) or bounds[2] <= bounds[0] or bounds[3] <= bounds[1]:
        raise ValueError('Verified world bounds are required')
    if not recipe.get('calibration', '').strip():
        raise ValueError('Record the source of the world bounds in calibration')
    if not recipe['raster'].lower().endswith('.edds'):
        raise ValueError('Supply the exact installed map raster resource')
    world = recipe['worldSuffix']
    if not world.lower().startswith('worlds/') or not world.lower().endswith('.ent') or '..' in world or '\\' in world:
        raise ValueError('Supply the exact recording world suffix')

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--map', help='Registered recipe name')
    parser.add_argument('--recipe', type=Path)
    parser.add_argument('--settings', type=Path, default=ROOT/'map-export.local.json')
    args = parser.parse_args()
    if not args.recipe and not args.map: args.map = input('Map recipe name: ').strip()
    if args.map and not re.fullmatch(r'[a-z0-9-]+', args.map): parser.error('Invalid map name')
    recipe = json.loads((args.recipe or ROOT/'scripts/maps'/f'{args.map}.json').read_text(encoding='utf-8-sig'))
    validate(recipe)
    settings = json.loads(args.settings.read_text(encoding='utf-8-sig'))
    output = Path(settings['output'])/(recipe['slug']+'-fallback')
    output.mkdir(parents=True, exist_ok=True)
    request = output/'raster-request.json'
    request.write_text(json.dumps(dict(mode='raster', raster=recipe['raster'])))
    subprocess.run(['dotnet','build',str(ROOT/'src/GC.MapExtract'),'-c','Release','--nologo'], check=True)
    extracted = subprocess.check_output(['dotnet',str(ROOT/'src/GC.MapExtract/bin/Release/net10.0/GC.MapExtract.dll'),str(request),str(output/'source-cache'),*settings['pakRoots']],text=True).strip().splitlines()[-1]
    decoded = output/'raster.dds'
    subprocess.run([settings.get('node','node'),str(ROOT/'scripts/decode-map-raster.mjs'),extracted,str(decoded)],check=True)
    dist = output/'dist'; (dist/'maps').mkdir(parents=True, exist_ok=True)
    with Image.open(decoded) as image:
        image.thumbnail((8192,8192))
        image.convert('RGB').save(dist/'maps/raster.webp', quality=92)
    config = dict(name=recipe['name'], worldSuffix=recipe['worldSuffix'], bounds=recipe['bounds'],
                  image='/maps/raster.webp', quality='raster', source='Installed game map raster',
                  provenance=dict(resource=recipe['raster'], sha256=hashlib.sha256(Path(extracted).read_bytes()).hexdigest(), calibration=recipe['calibration']))
    (dist/'playback-maps.json').write_text(json.dumps([config]))
    spec=importlib.util.spec_from_file_location('package',ROOT/'scripts/package-map.py')
    package=importlib.util.module_from_spec(spec);spec.loader.exec_module(package)
    package.build(dist,recipe['worldSuffix'],recipe['slug']+'-fallback',output/(recipe['slug']+'-fallback.zip'))
    print('Upload this ZIP in Administration > Playback maps. Full maps take priority.')

if __name__ == '__main__': main()
