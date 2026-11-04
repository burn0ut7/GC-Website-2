"""Match native footprints to live building bounds; package actual prefab previews.

Run GC_TestBuildingExport in the optional Lobby2 test addon first. The generated
capture queue is consumed through explicit MCP runtime calls, never game clients.
"""
import argparse
import csv
import hashlib
import json
import re
from pathlib import Path
from PIL import Image


def overlap(a, b):
    area = max(0, min(a[2], b[2]) - max(a[0], b[0])) * max(0, min(a[3], b[3]) - max(a[1], b[1]))
    union = (a[2]-a[0])*(a[3]-a[1]) + (b[2]-b[0])*(b[3]-b[1]) - area
    return area / union if union > 0 else 0


def attach_previews(profile, root, slug, export_name, catalog):
    import importlib.util
    spec=importlib.util.spec_from_file_location('map_previews',Path(__file__).with_name('map-previews.py'))
    previews=importlib.util.module_from_spec(spec);spec.loader.exec_module(previews)
    photos = previews.available(profile, export_name)
    output = root / 'dist/maps/buildings'
    output.mkdir(exist_ok=True)
    for key, model in catalog['models'].items():
        model.pop('image', None)
        photo = photos.get(model['resource'])
        if photo:
            with Image.open(photo) as image:
                w,h = image.size
                side = min(w,h)
                left,top = (w-side)//2,(h-side)//2
                image = image.crop((left, top, left+side, top+side))
                image.thumbnail((640,640), Image.Resampling.LANCZOS)
                image.convert('RGB').save(output / f'{key}.webp', quality=85)
            model['image'] = f'/maps/buildings/{key}.webp'
    (root/f'dist/maps/{slug}-buildings.json').write_text(json.dumps(catalog,separators=(',',':')),encoding='utf-8')


def finish_previews(profile, root, slug):
    catalog = json.loads((root/f'dist/maps/{slug}-buildings.json').read_text())
    for path, field in [(profile/f'GC_MapBuildings_{slug}.tsv','exportSha256'),
                        (root/f'dist/maps/{slug}-detail.json','detailSha256')]:
        with path.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != catalog[field]:
                raise ValueError('Footprints or source instances changed since matching. Rebuild the terrain phase.')
    attach_previews(profile, root, slug, slug, catalog)


def build(profile, root, slug="arland", export_name="Arland"):
    import math
    source = profile / f'GC_MapBuildings_{export_name}.tsv'
    with source.open(encoding='utf-8') as stream:
        rows = list(csv.DictReader(stream, delimiter='\t'))
    if len(rows) < 2:
        raise ValueError('Building export needs at least two candidate rows; run the complete Arland export first.')
    detail = root / f'dist/maps/{slug}-detail.json'
    detail_data = json.loads(detail.read_text())
    features = detail_data['features']
    bounds = [[float(r[k]) for k in ('minX','minZ','maxX','maxZ')] for r in rows]
    models, buildings, queue = {}, [], []
    bins = {}
    for i,b in enumerate(bounds):
        for x in range(math.floor(b[0]/128),math.floor(b[2]/128)+1):
            for z in range(math.floor(b[1]/128),math.floor(b[3]/128)+1):
                bins.setdefault((x,z),set()).add(i)
    # Runtime query order can change between Play sessions. Bind captures to the
    # resource sidecar, never assume a row index is a persistent model identity.
    def register(resource, index=-1, members=None, label=None):
        key = hashlib.sha256(resource.encode()).hexdigest()[:16]
        if key in models: return key
        stem = resource.rsplit('/',1)[-1].removesuffix('.et').removesuffix('.xob')
        name = re.sub(r'(?<=[a-z])(?=[A-Z])', ' ', stem).replace('_',' ')
        name = re.sub(r'\b(?:E|[0-9][A-Za-z0-9]*)\b', '', name)
        name = re.sub(r'^House (Wooden|Village|Town)', lambda m: m[1] + ' house', name)
        name = ' '.join(name.split())
        if label: name=label
        models[key] = {'name': name, 'resource': resource}
        queue.append({'index': index, 'id':key, 'resource':resource})
        if members is not None: queue[-1]['members']=members
        return key

    unresolved = []
    for f in features:
        if f['layer'] not in ('buildings', 'walls'):
            continue
        # The rendered footprint carries its binding. Never infer identity again
        # in the browser or choose the nearest model when an export is ambiguous.
        f.pop('model', None)
        f.pop('models', None)
        f.pop('ambiguous', None)
        p = f['points']
        b = [min(p[::2]), min(p[1::2]), max(p[::2]), max(p[1::2])]
        nearby=set()
        for x in range(math.floor(b[0]/128),math.floor(b[2]/128)+1):
            for z in range(math.floor(b[1]/128),math.floor(b[3]/128)+1):
                nearby.update(bins.get((x,z),()))
        candidates = sorted(((overlap(bounds[i],b), i) for i in nearby), reverse=True)
        if not candidates:
            unresolved.append({'layer':f['layer'], 'points':p})
            continue
        score, index = candidates[0]
        def instance_key(i):
            row=rows[i]
            return (row['resource'], *(row.get('t'+str(c),'') for c in range(4)), *bounds[i])
        runner = next((value for value, other in candidates[1:] if instance_key(other) != instance_key(index)),0)
        # Capture the actual overlapping assembly in its authored relative placement.
        if score < .65 or score - runner < .2:
            indices=sorted({i for value,i in candidates if value>=.25 and rows[i]['resource']},key=lambda i:(rows[i]['resource'],bounds[i]))
            if indices:
                indices=list({instance_key(i):i for i in indices}.values())
                if len(indices)==1:
                    key=register(rows[indices[0]]['resource'],indices[0])
                else:
                    import numpy as np
                    if len(indices)>64: raise ValueError('Assembly exceeds 64 objects; inspect the exported footprint.')
                    if any(not rows[i].get('t3') for i in indices):
                        raise ValueError('Overlapping previews require a fresh building export with instance transforms.')
                    def matrix(row):
                        result=np.eye(4)
                        for column in range(4):
                            values=[float(v) for v in re.findall(r'-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?',row[f't{column}'])]
                            if len(values)!=3: raise ValueError('Invalid exported instance transform')
                            result[:3,column]=values
                        return result
                    inverse=np.linalg.inv(matrix(rows[indices[0]]))
                    signature=[{'resource':rows[i]['resource'],'transform':np.round(inverse@matrix(rows[i]),4).tolist()} for i in indices]
                    resource='assembly:'+hashlib.sha256(json.dumps(signature,sort_keys=True,separators=(',',':')).encode()).hexdigest()
                    members=[{'index':i,'resource':rows[i]['resource']} for i in indices]
                    key=register(resource,members=members,label='Map structure')
                f['model']=key
                buildings.append({'points':p,'model':key})
            else:
                unresolved.append({'layer':f['layer'], 'points':p})
            continue
        resource = rows[index]['resource']
        if not resource:
            continue
        key = register(resource,index)
        f['model'] = key
        buildings.append({'points':p, 'model':key, 'overlap':round(score,4)})
    symbols_path = root/f'dist/maps/{slug}-symbols.json'
    symbols_export = profile/f'GC_MapSymbols_{export_name}.tsv'
    if symbols_path.exists() and symbols_export.exists():
        symbols=json.loads(symbols_path.read_text())
        with symbols_export.open(encoding='utf-8') as stream:
            resources={(r['type'],float(r['x']),float(r['z'])):r['resource'] for r in csv.DictReader(stream,delimiter='\t')}
        for symbol in symbols['symbols']:
            resource=resources.get((symbol['type'],symbol['x'],symbol['z']))
            if resource: symbol['model']=register(resource)
        symbols_path.write_text(json.dumps(symbols,separators=(',',':')))
    detail.write_text(json.dumps(detail_data,separators=(',',':')),encoding='utf-8')
    # Existing exports may already be split for loading. Update those same
    # footprints too, without regenerating unrelated terrain/water layers.
    bindings = {tuple(f['points']):f for f in features if f['layer'] in ('buildings','walls')}
    for part in detail.parent.glob(f'{slug}-detail-[0-9]*.json'):
        data = json.loads(part.read_text())
        for f in data['features']:
            if f['layer'] not in ('buildings','walls'): continue
            owner = bindings.get(tuple(f['points']), {})
            for field in ('model','models','ambiguous'):
                f.pop(field,None)
                if field in owner: f[field] = owner[field]
        part.write_text(json.dumps(data,separators=(',',':')),encoding='utf-8')
    result = {'version':1, 'source':'Arma Reforger 1.8.0.13; native TOPO + live prefab bounds',
              'exportSha256':hashlib.sha256(source.read_bytes()).hexdigest(),
              'detailSha256':hashlib.sha256(detail.read_bytes()).hexdigest(),
              'models':models,
              'coverage':{'matched':len(buildings),'unresolved':len(unresolved)}}
    attach_previews(profile, root, slug, export_name, result)
    (root/f'artifacts/map-tools/{slug}-unresolved-objects.json').write_text(json.dumps(unresolved),encoding='utf-8')
    (root/f'artifacts/map-tools/{slug}-building-capture-queue.json').write_text(json.dumps(queue),encoding='utf-8')
    print(f'{len(buildings)} matched footprints; {len(models)} models; {sum("image" in m for m in models.values())} previews')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('profile',type=Path)
    args = parser.parse_args()
    build(args.profile, Path(__file__).resolve().parent.parent)
