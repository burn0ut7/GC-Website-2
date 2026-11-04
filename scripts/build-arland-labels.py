"""Convert the optional Lobby2 map-label export into the static playback asset."""
import csv, hashlib, json, math, sys
from pathlib import Path
source=Path(sys.argv[1]); labels=[]
for row in csv.DictReader(source.read_text(encoding='utf-8-sig').splitlines(),delimiter='\t'):
    kind=row['type'].removeprefix('MDT_NAME_')
    x,z=float(row['x']),float(row['z'])
    if not (math.isfinite(x) and math.isfinite(z) and 0<=x<=4096 and 0<=z<=4096):
        raise ValueError('Position outside Arland')
    if not row['name'].strip() or len(row['name'])>160 or row['name'].startswith('#'):
        raise ValueError('Missing or unresolved place name')
    category='town' if kind in ('CITY','TOWN','VILLAGE','SETTLEMENT') else 'place' if kind=='GENERIC' else 'landscape'
    labels.append(dict(name=row['name'],x=x,z=z,category=category,type=kind))
if not labels: raise ValueError('No labels exported')
Path(sys.argv[2]).write_text(json.dumps(dict(version=1,source='Live Arland MapDescriptorComponent / MapItem.GetDisplayName; game 1.8.0.13; 2026-11-02',sha256=hashlib.sha256(source.read_bytes()).hexdigest(),labels=labels),indent=2),encoding='utf-8')
print(f'{len(labels)} map labels')
