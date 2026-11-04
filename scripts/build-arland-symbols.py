"""Build static Arland symbols from live descriptors and installed game icon configuration."""
import csv, hashlib, json, re, sys
from pathlib import Path
import numpy as np
from PIL import Image

def build(rows_path,defaults_path,visibility_path,imageset_path,atlas_path,output,slug="arland",extent=4096,all_styles=False):
    defaults=Path(defaults_path).read_text();visibility=Path(visibility_path).read_text();imageset=Path(imageset_path).read_text()
    styles={};quads={};views={}
    for block in re.findall(r'SCR_DescriptorViewLayer\s+"[^"\n]+"\s*\{([^{}]*)\}',visibility):
        kind=re.search(r'm_iDescriptorType\s+(\w+)',block);level=re.search(r'm_iViewLayer\s+(\d+)',block)
        if kind and level: views[kind[1]]=int(level[1])
    for block in re.findall(r'ImageSetDefClass\s+[^\n]+\{([^{}]*)\}',imageset):
        name=re.search(r'Name\s+"([^"]+)"',block);pos=re.search(r'Pos\s+(\d+)\s+(\d+)',block);size=re.search(r'Size\s+(\d+)\s+(\d+)',block)
        if name and pos and size:quads[name[1]]=tuple(map(int,(*pos.groups(),*size.groups())))
    for block in re.findall(r'SCR_DescriptorDefIcon(?:Text)?\s+"[^"\n]+"\s*\{([^{}]*)\}',defaults):
        kind=re.search(r'm_iDescriptorType\s+(\w+)',block);quad=re.search(r'm_sImageQuad\s+"([^"]+)"',block)
        if not kind or not quad:continue
        size=re.search(r'm_iIconSize\s+(\d+)',block);color=re.search(r'm_vColor\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)',block)
        styles[kind[1]]=dict(quad=quad[1],size=int(size[1]) if size else 32,color=list(map(float,color.groups())) if color else [1,1,1,1],rotate=bool(re.search(r'm_bAlignWithParent\s+1',block)),viewLayer=views.get(kind[1],0))
    atlas=Image.open(atlas_path).convert('RGBA');out=Path(output);(out/'icons/reforger').mkdir(parents=True,exist_ok=True)
    symbols=[];used={};unmapped={}
    if all_styles:
        for kind,style in styles.items():
            if style['quad'] not in quads: continue
            qx,qy,w,h=quads[style['quad']];pixels=np.asarray(atlas.crop((qx,qy,qx+w,qy+h)),dtype=float)
            pixels*=np.asarray(style['color']);Image.fromarray(np.uint8(np.clip(pixels,0,255))).save(out/f'icons/reforger/topo-{kind}.png')
            used[kind]=dict(**style,url=f'/icons/reforger/topo-{kind}.png')
    rows=[] if all_styles else csv.DictReader(Path(rows_path).read_text(encoding='utf-8-sig').splitlines(),delimiter='\t')
    for row in rows:
        kind=row['type'];style=styles.get(kind)
        if not style or style['quad'] not in quads:
            unmapped[kind]=unmapped.get(kind,0)+1;continue
        x,z,yaw=[float(row[k]) for k in ('x','z','yaw')]
        if not np.isfinite([x,z,yaw]).all() or not(0<=x<=extent and 0<=z<=extent):raise ValueError('Invalid symbol position')
        if kind not in used:
            qx,qy,w,h=quads[style['quad']];pixels=np.asarray(atlas.crop((qx,qy,qx+w,qy+h)),dtype=float)
            pixels*=np.asarray(style['color']);Image.fromarray(np.uint8(np.clip(pixels,0,255))).save(out/f'icons/reforger/topo-{kind}.png')
            used[kind]=dict(**style,url=f'/icons/reforger/topo-{kind}.png')
        symbols.append(dict(type=kind,x=x,z=z,yaw=yaw))
    inputs=[defaults_path,visibility_path,imageset_path,atlas_path] if all_styles else [rows_path,defaults_path,visibility_path,imageset_path,atlas_path]
    result=dict(version=1,styles=used,symbols=symbols,unmapped=unmapped,sourceHashes={Path(p).name:hashlib.sha256(Path(p).read_bytes()).hexdigest() for p in inputs})
    (out/f'maps/{slug}-symbols.json').write_text(json.dumps(result,indent=2));print(json.dumps(dict(symbols=len(symbols),types=len(used),unmapped=unmapped)))
if __name__=='__main__':build(*sys.argv[1:])
