"""Shared, content-versioned previews with session receipts for fresh captures."""
import hashlib
import json
import time
from functools import lru_cache
from PIL import Image
import shutil
from pathlib import Path

RECIPE = 'lod0-fitted-blue-single-visible-v11'

@lru_cache(maxsize=2048)
def _valid_photo(path,size,modified):
    try:
        with Image.open(path) as image:
            if image.format!='PNG': return False
            image.verify()
        return True
    except (OSError,ValueError,SyntaxError): return False

def valid_photo(path):
    try:
        stat=path.stat()
        return _valid_photo(str(path),stat.st_size,stat.st_mtime_ns)
    except OSError: return False

def configure_library(profile, name, roots):
    """Conservative content revision of installed archives, independent of the map.
    Include every game/mod asset root used by the export. A changed archive
    invalidates the library instead of silently reusing a same-named prefab.
    """
    extensions={'.pak','.et','.ct','.xob','.edds','.emat','.gamemat','.conf','.c'}
    files = sorted({p.resolve() for root in roots for p in Path(root).rglob('*') if p.is_file() and p.suffix.lower() in extensions and '.git' not in p.parts})
    if not files: raise ValueError('Preview source roots contain no installed PAK archives.')
    digest=hashlib.sha256(RECIPE.encode())
    for path in files:
        with path.open('rb') as stream: content=hashlib.file_digest(stream,'sha256').hexdigest()
        digest.update(path.name.lower().encode());digest.update(content.encode())
    marker=Path(profile)/f'GC_MapExport_{name}.json'
    context={'revision':digest.hexdigest(),'recipe':RECIPE,'session':marker.stat().st_mtime_ns}
    target=marker.with_suffix('.previews.json')
    previous=json.loads(target.read_text()) if target.exists() else {}
    context['captureAfter']=previous.get('captureAfter',time.time_ns()) if all(previous.get(k)==v for k,v in context.items()) else time.time_ns()
    target.write_text(json.dumps(context))
    return context['revision']

def shared_cache(profile,name):
    marker=Path(profile)/f'GC_MapExport_{name}.json'
    context=marker.with_suffix('.previews.json')
    if not context.exists(): return None
    data=json.loads(context.read_text())
    if data.get('recipe')!=RECIPE or data.get('session')!=marker.stat().st_mtime_ns: return None
    return Path(profile)/'GC_MapPreviewLibrary'/data['revision']

def cache_for(profile, name):
    marker=Path(profile)/f'GC_MapExport_{name}.json'
    if not marker.exists(): raise ValueError('Configure a fresh Workbench export before building previews.')
    context_path=marker.with_suffix('.previews.json')
    if context_path.exists() and json.loads(context_path.read_text()).get('recipe')!=RECIPE:
        raise ValueError('Capture recipe changed; configure the preview library again before resuming.')
    session=f'{marker.stat().st_mtime_ns}-{hashlib.sha256(marker.read_bytes()).hexdigest()[:16]}'
    started=marker.stat().st_mtime_ns
    if shared_cache(profile,name):
        context=json.loads(marker.with_suffix('.previews.json').read_text())
        session+='-'+context['revision']
        started=max(started,context.get('captureAfter',started))
    return Path(profile)/'GC_MapPreviewCache'/name/session,started

def available(profile, name):
    cache,started=cache_for(profile,name);photos={}
    for receipt in [*Path(profile).glob('GC_BuildingPhoto_*.resource'),*cache.glob('*.resource')]:
        image=receipt.with_suffix('.png')
        if receipt.stat().st_mtime_ns<started or not valid_photo(image) or image.stat().st_mtime_ns<receipt.stat().st_mtime_ns: continue
        resource=receipt.read_text().strip()
        if resource not in photos or image.stat().st_mtime_ns>photos[resource].stat().st_mtime_ns: photos[resource]=image
    shared=shared_cache(profile,name)
    if shared:
        for receipt in shared.glob('*.resource'):
            image=receipt.with_suffix('.png')
            if valid_photo(image): photos.setdefault(receipt.read_text().strip(),image)
    return photos

def preserve(profile,name):
    cache,_=cache_for(profile,name)
    destination_root=shared_cache(profile,name) or cache
    destination_root.mkdir(parents=True,exist_ok=True)
    for resource,image in available(profile,name).items():
        key=hashlib.sha256(resource.encode()).hexdigest();destination=destination_root/f'{key}.png'
        if image.resolve()==destination.resolve(): continue
        if valid_photo(destination) and destination.with_suffix('.resource').exists() and destination.stat().st_mtime_ns>=image.stat().st_mtime_ns: continue
        shutil.copy2(image,destination);shutil.copy2(image.with_suffix('.resource'),destination.with_suffix('.resource'))


def preserve_capture(profile,name,receipt):
    """Copy only the just-completed capture, avoiding a growing full-library scan."""
    receipt=Path(receipt);image=receipt.with_suffix('.png');cache,started=cache_for(profile,name)
    if not valid_photo(image): raise ValueError('Incomplete preview capture')
    if receipt.stat().st_mtime_ns<started or image.stat().st_mtime_ns<receipt.stat().st_mtime_ns:
        raise ValueError('Stale preview capture')
    key=hashlib.sha256(receipt.read_text().strip().encode()).hexdigest()
    for folder in (shared_cache(profile,name) or cache,):
        if folder:
            folder.mkdir(parents=True,exist_ok=True)
            shutil.copy2(image,folder/f'{key}.png');shutil.copy2(receipt,folder/f'{key}.resource')
