"""Register a game raster using explicitly verified world bounds (north-up X/Z).

This imports a base map, not generated scenery, depth or building previews.
Decode the original EDDS with decode-map-raster.mjs first. Never infer world
dimensions from pixel resolution: obtain Size()/Offset() from the live map.
"""
import argparse
import hashlib
import json
import math
import re
from pathlib import Path
from PIL import Image


def register(image, original, name, slug, world, bounds, evidence, root):
    if not re.fullmatch(r'[a-z0-9]+(?:-[a-z0-9]+)*', slug):
        raise ValueError('Use a lowercase map slug')
    world = world.replace('\\', '/').lower()
    if not world.startswith('worlds/') or not world.endswith('.ent') or '..' in world:
        raise ValueError('Supply the exact calibrated world suffix')
    if len(bounds) != 4 or not all(math.isfinite(v) for v in bounds) or bounds[2] <= bounds[0] or bounds[3] <= bounds[1]:
        raise ValueError('Invalid X/Z bounds')
    if not evidence.strip() or not name.strip():
        raise ValueError('Map name and calibration evidence are required')
    manifest = root / 'dist/playback-maps.json'
    maps = json.loads(manifest.read_text(encoding='utf-8'))
    target = root / f'dist/maps/{slug}.webp'
    source = dict(originalSha256=hashlib.sha256(original.read_bytes()).hexdigest(),
                  decodedSha256=hashlib.sha256(image.read_bytes()).hexdigest(), calibration=evidence)
    with Image.open(image) as raster:
        raster.convert('RGB').save(target, quality=90)
        source['pixels'] = list(raster.size)
    entry = dict(name=name, worldSuffix=world, bounds=bounds, image=f'/maps/{slug}.webp',
                 source=evidence, provenance=source)
    index = next((i for i, m in enumerate(maps) if m['worldSuffix'].lower() == world), None)
    if index is None:
        maps.append(entry)
    else:
        # Reimporting the base image must not discard independently built layers.
        maps[index].update(entry)
    manifest.write_text(json.dumps(maps, indent=2) + '\n', encoding='utf-8')
    print(f'Registered {name}: {world}, bounds={bounds}')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('image', type=Path)
    p.add_argument('original', type=Path)
    for key in ('name', 'slug', 'world', 'evidence'):
        p.add_argument('--' + key, required=True)
    p.add_argument('--bounds', nargs=4, type=float, required=True)
    a = p.parse_args()
    register(a.image, a.original, a.name, a.slug, a.world, a.bounds, a.evidence, Path(__file__).resolve().parent.parent)
