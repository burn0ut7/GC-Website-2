"""Package a built Workbench map and every referenced image into a portable ZIP.
Usage: python scripts/package-map.py --root dist --world WORLD_SUFFIX --id arland --output arland.zip
No server credentials, original game archives or local file paths are included.
"""
import argparse, hashlib, json, re, zipfile
from pathlib import Path

def build(root, world, identifier, output):
    root = Path(root).resolve()
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,63}', identifier):
        raise ValueError('Use a lowercase map identifier with letters, numbers and hyphens.')
    matches = [m for m in json.loads((root/'playback-maps.json').read_text()) if m['worldSuffix'].lower() == world.lower()]
    if len(matches) != 1: raise ValueError('Select exactly one registered world suffix.')
    assets, visiting = {}, set()
    def collect(value):
        if isinstance(value, dict): return {k: collect(v) for k,v in value.items()}
        if isinstance(value, list): return [collect(v) for v in value]
        if isinstance(value, str) and value.startswith(('/maps/', '/icons/')):
            name = value[1:]
            if not re.fullmatch(r'[a-zA-Z0-9_-]+(?:/[a-zA-Z0-9_-]+)*\.(json|webp|png)',name): raise ValueError('Unsupported asset path: '+name)
            path = (root/name).resolve()
            if not path.is_relative_to(root): raise ValueError('Asset outside export folder')
            if name not in assets:
                if name in visiting: raise ValueError('Circular JSON asset reference')
                visiting.add(name)
                data = path.read_bytes()
                if path.suffix == '.json': data = json.dumps(collect(json.loads(data)),separators=(',',':')).encode()
                assets[name] = data
                visiting.remove(name)
            return '@map/'+name
        return value
    config = collect(matches[0])
    manifest = dict(format='gc-map',version=1,id=identifier,map=config,files=[dict(path=p,bytes=len(b),sha256=hashlib.sha256(b).hexdigest()) for p,b in sorted(assets.items())])
    output = Path(output); output.parent.mkdir(parents=True,exist_ok=True)
    temporary = output.with_suffix('.partial')
    with zipfile.ZipFile(temporary,'w',zipfile.ZIP_DEFLATED) as archive:
        archive.writestr('manifest.json',json.dumps(manifest,indent=2))
        for path,data in sorted(assets.items()): archive.writestr(path,data)
    temporary.replace(output)
    print(f'{output}: {len(assets)} assets, {output.stat().st_size:,} bytes')

if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root',type=Path,default=Path('dist'))
    parser.add_argument('--world',required=True)
    parser.add_argument('--id',required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args(); build(args.root,args.world,args.id,args.output)
