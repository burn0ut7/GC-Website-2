"""Manual Workbench capture through the existing Reforger MCP service.
Open the map's Lobby2 test world, start Play, then run this script. Never saves worlds.
Uses the configured shared MCP service; does not start a second service or Workbench.
"""
import argparse, csv, importlib.util, json, time, urllib.request
from pathlib import Path

class Workbench:
    def __init__(self, configuration, process):
        config_path=Path(configuration).resolve();config=json.loads(config_path.read_text(encoding='utf-8-sig'))
        http=config['localHttp'];key=Path(http['tokenFile'])
        if not key.is_absolute(): key=config_path.parent/key
        self.url=f"http://127.0.0.1:{http['port']}/mcp"
        self.token=key.read_text().strip();self.process=process;self.sequence=0
        self.rpc('initialize',dict(protocolVersion='2024-11-05',capabilities={},clientInfo=dict(name='gc-map-export',version='1')))

    def rpc(self, method, params):
        self.sequence+=1
        request=urllib.request.Request(self.url,json.dumps(dict(jsonrpc='2.0',id=self.sequence,method=method,params=params)).encode(),
            headers={'Authorization':'Bearer '+self.token,'Content-Type':'application/json','Accept':'application/json, text/event-stream'})
        with urllib.request.urlopen(request,timeout=180) as response: text=response.read().decode()
        if text.startswith('event:') or text.startswith('data:'):
            messages=[json.loads(line[5:]) for line in text.splitlines() if line.startswith('data:')]
            reply=next(m for m in messages if m.get('id')==self.sequence)
        else: reply=json.loads(text)
        if 'error' in reply: raise RuntimeError('MCP request failed: '+str(reply['error']))
        return reply['result']

    def tool(self, name, arguments):
        reply=self.rpc('tools/call',dict(name=name,arguments=arguments))
        if reply.get('isError'): raise RuntimeError(str(reply.get('content')))
        return json.loads(next(c['text'] for c in reply['content'] if c['type']=='text'))

    def call(self, method, arguments=None):
        reply=self.tool('workbench_runtime_call',dict(processId=self.process,entityName='GameMode_Lobby2',componentType='GC_LobbyManager',method=method,returnType='string',arguments=arguments or []))
        while reply.get('operationCompleted') is False:
            time.sleep(.25);reply=self.tool('workbench_operation',dict(operationId=reply['operationId']))
        if reply.get('state')!='invoked': raise RuntimeError(str(reply))
        return reply['returned']

def capture_ocean(wb, world, output):
    identity, enabled, level = wb.call('GC_TestMapOceanInfo').split('\t')
    if identity != world or enabled not in ('0','1','true','false'):
        raise ValueError('Unexpected ocean calibration')
    data = dict(world=identity, level=float(level) if enabled in ('1','true') else None)
    target = Path(output)/'ocean.json'
    temporary = target.with_suffix('.tmp')
    temporary.write_text(json.dumps(data, allow_nan=False))
    temporary.replace(target)
    return data


def run(args):
    wb=Workbench(args.configuration,args.process)
    profile=Path(args.profile);slug=args.map
    if not args.resume:
        result=wb.call('GC_TestMapExportConfigure',[dict(type='string',value=slug)])
        if result!='configured=true': raise RuntimeError(result)
    metadata=json.loads((profile/f'GC_MapExport_{slug}.json').read_text())
    info=wb.call('GC_TestPlaybackMapInfo')
    if metadata['world'] not in info: raise RuntimeError('Export metadata does not match the running world')
    if args.phase=='capture':
        for method in ('GC_TestTopographyExport','GC_TestSceneryExportStart','GC_TestMapLabelsExport','GC_TestMapSymbolsExport','GC_TestBuildingExportStart'):
            result=wb.call(method);print(method,result,flush=True)
            if method=='GC_TestSceneryExportStart' and result=='started':
                deadline=time.monotonic()+900
                while time.monotonic()<deadline:
                    result=(profile/f'GC_MapScenery_{slug}.status').read_text().strip()
                    if result and result!='running': break
                    time.sleep(1)
                print(result,flush=True)
            if method=='GC_TestBuildingExportStart' and result=='started':
                deadline=time.monotonic()+900
                while time.monotonic()<deadline:
                    result=(profile/f'GC_MapBuildings_{slug}.status').read_text().strip()
                    if result and result!='running': break
                    time.sleep(1)
                print(result,flush=True)
            if not (result=='sampling' or result.startswith('exported=')): raise RuntimeError(result)
        deadline=time.monotonic()+600
        while time.monotonic()<deadline:
            result=wb.call('GC_TestTopographyStatus')
            if result.startswith('saved=true'): print(result,flush=True);break
            if 'saved=false' in result: raise RuntimeError(result)
            time.sleep(1)
        else: raise RuntimeError('Height export timed out')
    elif args.phase=='water':
        capture_ocean(wb,metadata['world'],Path(args.output).parent)
        result=wb.call('GC_TestRiverSurfaceExport')
        if not result.startswith('vertices='): raise RuntimeError(result)
        surface_path=profile/f'GC_RiverSurfaces_{slug}.tsv'
        spec=importlib.util.spec_from_file_location('river_probes',Path(__file__).with_name('river-surface-probes.py'))
        river_probes=importlib.util.module_from_spec(spec);spec.loader.exec_module(river_probes)
        lines=river_probes.add_surface_hints(Path(args.input).read_text().splitlines(),surface_path)
        Path(args.output).with_name('river-surfaces.tsv').write_bytes(surface_path.read_bytes())
        Path(args.output).with_name('water-probes-with-surfaces.tsv').write_text('\n'.join(lines)+'\n')
        output=Path(args.output);temporary=output.with_suffix('.partial')
        with temporary.open('w') as combined:
            for start in range(0,len(lines),25000):
                (profile/'GC_WaterProbePoints.tsv').write_text('\n'.join(lines[start:start+25000])+'\n')
                result=wb.call('GC_TestWaterDepthExport')
                if not result.startswith('samples='): raise RuntimeError(result)
                sampled=(profile/f'GC_WaterDepth_{slug}.tsv').read_text().splitlines()
                if len(sampled)-1!=len(lines[start:start+25000]): raise RuntimeError('Incomplete water batch')
                combined.write('\n'.join(sampled if start==0 else sampled[1:])+'\n')
                print(f'Water {min(start+25000,len(lines))}/{len(lines)}',flush=True)
            if not lines: combined.write('x\tz\tground\tsurface\ttype\twet\n')
        temporary.replace(output)
    elif args.phase=='photos':
        queue=json.loads(Path(args.input).read_text())
        spec=importlib.util.spec_from_file_location('map_previews',Path(__file__).with_name('map-previews.py'))
        previews=importlib.util.module_from_spec(spec);spec.loader.exec_module(previews)
        previews.preserve(profile,slug)
        existing=set(previews.available(profile,slug))
        pending=[item for item in queue if args.recapture or item['resource'] not in existing]
        for item in pending:
            if item.get('members'):
                result=wb.call('GC_TestBuildingPhotoAssembly',[
                    dict(type='string',value=item['resource']),
                    dict(type='string',value=','.join(str(m['index']) for m in item['members'])),
                    dict(type='string',value='\n'.join(m['resource'] for m in item['members']))])
                if result!='registered': raise RuntimeError('Could not register assembly: '+result)
        try:
            for start in range(0,len(pending),args.batch_size):
                batch=pending[start:start+args.batch_size]
                request=profile/f'GC_PhotoBatch_{slug}.txt'
                temporary=request.with_suffix('.tmp')
                temporary.write_text('\n'.join(item['resource'] for item in batch), encoding='utf-8')
                temporary.replace(request)
                result=wb.call('GC_TestBuildingPhotoBatchFromFile')
                if result!='prepared='+str(len(batch)): raise RuntimeError('Could not prepare previews: '+result)
                time.sleep(2)
                for offset,item in enumerate(batch):
                    result=wb.call('GC_TestBuildingPhotoResource',[dict(type='string',value=item['resource'])])
                    if result=='no-visual':
                        print('No physical model: '+item['resource'],flush=True)
                        continue
                    if not result.startswith('index='): raise RuntimeError('Could not position model: '+result)
                    index=int(result[6:]);arg=[dict(type='int',value=index)]
                    # Geometry streams after positioning the camera. The engine's
                    # preload completion owns readiness, not a half-second delay.
                    deadline=time.monotonic()+35
                    while True:
                        time.sleep(.5)
                        saved=wb.call('GC_TestBuildingPhotoSave',arg)
                        if saved=='saved=true': break
                        if saved!='loading' or time.monotonic()>deadline: raise RuntimeError('Screenshot not ready: '+saved)
                    receipt=profile/f'GC_BuildingPhoto_{index}.resource';image=receipt.with_suffix('.png')
                    deadline=time.monotonic()+15
                    while True:
                        if image.exists() and image.stat().st_mtime_ns>=receipt.stat().st_mtime_ns:
                            with image.open('rb') as stream:
                                if image.stat().st_size>=12:
                                    stream.seek(-12,2)
                                    if stream.read()==b'\x00\x00\x00\x00IEND\xaeB`\x82': break
                        if time.monotonic()>deadline: raise RuntimeError('Screenshot was not completed')
                        time.sleep(.1)
                    previews.preserve_capture(profile,slug,receipt)
                    print(f'Preview {start+offset+1}/{len(pending)}',flush=True)
        finally:
            print(wb.call('GC_TestBuildingPhotoEnd'),flush=True)

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--configuration',required=True);p.add_argument('--process',required=True,type=int)
    p.add_argument('--profile',required=True);p.add_argument('--map',required=True)
    p.add_argument('--phase',choices=['capture','water','photos'],default='capture')
    p.add_argument('--resume',action='store_true');p.add_argument('--input');p.add_argument('--output')
    p.add_argument('--batch-size', type=int, choices=range(1,51), default=50)
    p.add_argument('--recapture', action='store_true', help='Recapture the selected previews even if cached')
    run(p.parse_args())
