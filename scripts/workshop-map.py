"""Prepare an isolated Workbench project for a single installed workshop terrain."""
import hashlib
import json
import math
import re
import subprocess
import time
from pathlib import Path


def workshop_root(settings):
    return Path(settings.get('workshopRoot', Path(settings['profile']).parents[1] / 'ArmaReforger/addons'))


def find_recipe(settings, root, slug):
    """Reuse the native scenario's wiring only when it identifies a unique setup."""
    matches = []
    for project in workshop_root(settings).glob('*/addon.gproj'):
        header = project.read_text(encoding='utf-8-sig', errors='replace')
        fields = dict(re.findall(r'^\s*(ID|GUID|TITLE)\s+"([^"]+)"', header, re.M))
        names = [re.sub('[^a-z0-9]+', '-', fields.get(k, '').lower()).strip('-') for k in ('ID', 'TITLE', 'GUID')]
        if slug in names:
            matches.append((project.parent, fields))
    if len(matches) != 1:
        raise ValueError(f'Expected one installed workshop terrain named {slug}. Use its workshop ID or an explicit recipe.')
    mod, fields = matches[0]
    dll = root/'src/GC.MapExtract/bin/Release/net10.0/GC.MapExtract.dll'
    wiring = json.loads(subprocess.check_output(['dotnet', str(dll), '--discover-workshop', '.', str(mod)], text=True))
    canonical_slug = re.sub('[^a-z0-9]+', '-', fields['ID'].lower()).strip('-')
    return dict(name=fields.get('TITLE', fields['ID']), slug=canonical_slug, workshopId=fields['GUID'], **wiring)


def prepare(settings, spec):
    profile = Path(settings['profile'])
    addons = profile.parent / 'addons'
    installed = workshop_root(settings)
    matches = list(installed.glob('*_' + spec['workshopId']))
    if len(matches) != 1:
        raise ValueError(f"Expected one installed addon for {spec['workshopId']} under {installed}")
    mod = matches[0].resolve()
    link = addons / mod.name
    if not link.exists():
        import _winapi
        _winapi.CreateJunction(str(mod), str(link))
    if link.resolve() != mod:
        raise ValueError(f'Existing Workbench addon points elsewhere: {link}')
    slug = spec['slug']
    project_id = 'GCMapExport' + slug.replace('-', '_')
    folder = addons / project_id
    world = f'worlds/playback/{slug}_playback.ent'
    def guid(kind):
        return hashlib.sha256((project_id + '/' + kind).encode()).hexdigest()[:16].upper()
    def create(path, content):
        path = folder / path
        path.parent.mkdir(parents=True, exist_ok=True)
        if path.exists():
            if path.read_text() != content:
                raise ValueError(f'Preserving modified export fixture: {path}')
        else:
            path.write_text(content)
    create('addon.gproj', f'GameProject {{\n ID "{project_id}"\n GUID "{guid("project")}"\n TITLE "{project_id}"\n Dependencies {{\n  "6A115D5DFF7ED8A1"\n  "{spec["workshopId"]}"\n }}\n}}\n')
    create(world, f'SubScene {{\n Parent "{spec["parentWorld"]}"\n}}\n')
    create(world + '.meta', f'MetaFileClass {{\n Name "{{{guid("world")}}}{world}"\n}}\n')
    entities = [
        ('SCR_AIWorld', spec['aiWorld']),
        ('SCR_FactionManager Factions', '{589540B35EBA8CDC}Prefabs/MP/Managers/Factions/FactionManager_USxUSSR.et'),
        ('GC_LobbyGameMode GameMode_Lobby2', '{6A4D100000000010}Prefabs/MP/Modes/GameMode_Lobby_Example.et'),
        ('RadioManagerEntity', '{B8E09FAB91C4ECCD}Prefabs/Systems/Radio/RadioManager.et'),
        ('ScriptedChatEntity', '{6A63000000000001}Prefabs/MP/GC_LobbyChat.et'),
    ]
    if spec.get('mapEntity'):
        entities.append(('SCR_MapEntity', spec['mapEntity']))
    layer = ''.join(f'{kind} : "{prefab}" {{\n coords 0 0 0\n}}\n' for kind, prefab in entities)
    create(world[:-4] + '_Layers/default.layer', layer)
    return folder/'addon.gproj', '$' + project_id + ':' + world, mod


def operation(wb, name, args, allow_missing_world=False):
    reply = wb.tool(name, args)
    while reply.get('operationCompleted') is False or reply.get('state') == 'pending':
        time.sleep(.5)
        reply = wb.tool('workbench_operation', {'operationId': reply['operationId']})
    if allow_missing_world and reply.get('state') == 'world_unavailable':
        return reply
    if reply.get('state') not in ('opened', 'started', 'stopped', 'observed', 'already_started', 'already_stopped'):
        raise RuntimeError(f'{name}: {reply}')
    return reply


def project_titles(project):
    names = re.findall(r'^\s*(?:ID|TITLE)\s+"([^"]+)"', project.read_text(encoding='utf-8-sig'), re.M)
    return {'Enfusion Workbench - ' + name for name in names}


def wait_project(wb, project, process):
    titles = project_titles(project)
    deadline = time.monotonic() + 120
    while time.monotonic() < deadline:
        instances = wb.tool('workbench_status', {})['workbench']['instances']
        if any(i['processId'] == process and i['responding'] and i['windowTitle'] in titles for i in instances):
            observed = wb.tool('workbench_windows', {'details': False})
            if observed.get('processId') == process and any(w['title'] in titles and w['enabled'] for w in observed['windows']):
                return
        time.sleep(1)
    raise RuntimeError(f'Workbench did not show the requested project {project} in process {process}.')


def release_idle_terrain(wb, mod):
    """Switch from the selected terrain's idle resource browser. Preserve open editors."""
    instances = wb.tool('workbench_status', {})['workbench']['instances']
    if len(instances) != 1:
        return None
    project = mod/'addon.gproj'
    titles = project_titles(project)
    view = wb.tool('workbench_windows', {'details': True})
    windows = view.get('windows', [])
    if len(windows) != 1 or windows[0].get('automationId') != 'Arma4WorkbenchApp.ResourceManagerMain':
        return None
    main = windows[0]
    if main['title'] not in titles or not main.get('canClose') or main.get('modal') or main.get('unsavedTabs'):
        return None
    wb.process = instances[0]['processId']
    if wb.process != view['processId']:
        raise RuntimeError('Workbench changed during project inspection')
    print('Temporarily switching from the idle terrain project to its export project.', flush=True)
    close_project(wb, main['title'])
    return project


def start(wb, project, resource, process):
    instances = wb.tool('workbench_status', {})['workbench']['instances']
    windows = wb.tool('workbench_windows', {'details': False}).get('windows', []) if len(instances) == 1 else []
    if not any(w['title'] == 'Enfusion Workbench - ' + project.parent.name for w in windows):
        raise ValueError(f'Close the current Workbench project normally and rerun to open {project}')
    wb.process = instances[0]['processId']
    if process is not None and wb.process != process:
        raise RuntimeError('Workbench process changed before opening the export world.')
    state = operation(wb, 'workbench_play_status', {'processId': wb.process}, allow_missing_world=True)
    if state.get('isPlaying') is True:
        raise ValueError('Stop existing Play before a fresh export')
    opened = operation(wb, 'workbench_open_world', {'processId': wb.process, 'resourceName': resource})
    if opened.get('loadedWorld', '').lower() != resource.lower():
        raise ValueError(f'Unexpected Workbench world: {opened}')
    operation(wb, 'workbench_play', {'processId': wb.process, 'action': 'start'})


def stop(wb, resource):
    status = operation(wb, 'workbench_play_status', {'processId': wb.process})
    if status.get('isPlaying') is False:
        return
    info = wb.call('GC_TestPlaybackMapInfo')
    if not info.lower().startswith('world=' + resource.lower() + ' size='):
        raise RuntimeError('Cannot verify the active export world for cleanup.')
    operation(wb, 'workbench_play', {'processId': wb.process, 'action': 'stop'})


def close_project(wb, title):
    observed = wb.tool('workbench_windows', {'details': True})
    windows = observed['windows']
    main = next((w for w in windows if w['title'] == title), None)
    if observed.get('processId') != wb.process or not main or not main.get('canClose') or any(
        w.get('modal') or w.get('unsavedTabs') for w in windows
    ):
        raise RuntimeError('The export project has open work or a dialog. Close it normally.')
    closing = wb.tool('workbench_window_action', {'selectionId': main['windowId'], 'action': 'close'})
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        instances = wb.tool('workbench_status', {})['workbench']['instances']
        if not any(i['processId'] == wb.process for i in instances):
            return
        if closing.get('state') == 'pending':
            closing = wb.tool('workbench_window_action_status', {'operationId': closing['operationId']})
        time.sleep(.5)
    raise RuntimeError('The generated project is still open. Inspect Workbench for a prompt.')


def recording_worlds(settings, parent):
    # Match the authored SubScene parent resource, including its GUID.
    worlds = {parent.split('}', 1)[1]}
    pattern = re.compile(r'^\s*SubScene\s*\{\s*Parent\s+"' + re.escape(parent) + r'"', re.I)
    for root in settings.get('missionRoots', []):
        directory = Path(root)
        if not (directory/'worlds').is_dir():
            raise ValueError(f'Missing mission worlds under {directory}')
        for world in (directory/'worlds').rglob('*.ent'):
            with world.open('rb') as source:
                header = source.read(4096).decode('utf-8-sig', errors='replace')
            if pattern.match(header):
                worlds.add(world.relative_to(directory).as_posix())
    return sorted(worlds, key=str.lower)


def discover(root, output, spec, mod, info, resource):
    print('Live calibration:', info, flush=True)
    suffix = resource.split(':', 1)[1]
    if not info.lower().startswith('world=' + resource.lower() + ' size='):
        raise ValueError('Unexpected running world: ' + info)
    def vector(key):
        match = re.search(key + r'=([\d.eE+ ,<>-]+)', info)
        return [float(v) for v in re.findall(r'-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?', match[1])]
    size, offset = vector('size'), vector('offset')
    if offset[0] or offset[2] or size[0] != size[2]:
        raise ValueError('Unsupported live calibration: ' + info)
    dll = root/'src/GC.MapExtract/bin/Release/net10.0/GC.MapExtract.dll'
    terrain = json.loads(subprocess.check_output(['dotnet', str(dll), '--terrain', spec['mapEntity'].split('}',1)[1], str(mod)], text=True))
    columns = math.isqrt(terrain['tileCount'])
    if columns**2 != terrain['tileCount'] or columns * 256 != size[0]:
        raise ValueError('Terrain tiles do not form a square')
    recipe = dict(spec, **terrain, tileColumns=columns, bounds=[0,0,int(size[0]),int(size[2])],
                  worldSuffix=suffix, calibration=info)
    target = output/'resolved-recipe.json'
    target.write_text(json.dumps(recipe, indent=2))
    return target, recipe

