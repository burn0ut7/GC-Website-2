"""One manual export: active Workbench world -> verified portable map ZIP."""
import argparse, importlib.util, json, re, subprocess, sys
from pathlib import Path

ROOT=Path(__file__).resolve().parent.parent
def run(command): subprocess.run([str(s) for s in command],cwd=ROOT,check=True)
def load(file):
    spec=importlib.util.spec_from_file_location(file.replace('-','_'),ROOT/'scripts'/file)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module);return module

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--settings',type=Path,default=ROOT/'map-export.local.json')
    parser.add_argument('--map',help='Recipe name, e.g. everon, kolguyev or arland')
    parser.add_argument('--batch-size', type=int, choices=range(1,51), default=50, metavar='1..50', help='Number of object previews staged together')
    parser.add_argument('--recipe',type=Path,help='A custom map recipe')
    args=parser.parse_args()
    if not args.settings.exists(): raise SystemExit('Copy map-export.example.json to map-export.local.json and set your local paths first.')
    settings=json.loads(args.settings.read_text(encoding='utf-8-sig'))
    run(['dotnet','build',ROOT/'src/GC.MapExtract','-c','Release','--nologo'])
    helper=load('workshop-map.py')
    if args.recipe:
        recipe_path=args.recipe
        recipe=json.loads(recipe_path.read_text(encoding='utf-8-sig'))
    else:
        name=(args.map or input('Map name or workshop ID: ')).strip().lower()
        if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,63}',name):
            raise SystemExit('Use a map slug or workshop ID.')
        recipe_path=ROOT/'scripts/maps'/f'{name}.json'
        recipe=json.loads(recipe_path.read_text()) if recipe_path.is_file() else helper.find_recipe(settings,ROOT,name)
    slug=recipe['slug']
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,63}',slug): raise SystemExit('Invalid map identifier in recipe.')
    capture=load('workbench-map-capture.py');wb=capture.Workbench(settings['mcpConfiguration'],0)
    profile=Path(settings['profile']);output=Path(settings['output'])/slug
    output.mkdir(parents=True,exist_ok=True)
    workshop = 'workshopId' in recipe
    if workshop:
        project, resource, mod=helper.prepare(settings, recipe)
        recipe["worldAliases"]=helper.recording_worlds(settings, recipe["parentWorld"])
        previous_project=helper.release_idle_terrain(wb,mod)
        launched_pid=None
        started=False
        try:
            if not wb.tool('workbench_status',{})['workbench']['instances']:
                launched_pid=wb.tool('workbench_open_project',{'projectPath':str(project)})['processId']
                wb.process=launched_pid
                helper.wait_project(wb,project,launched_pid)
            helper.start(wb, project, resource, launched_pid)
            started=True
            recipe_path, recipe=helper.discover(ROOT,output,recipe,mod,wb.call('GC_TestPlaybackMapInfo'),resource)
            settings['pakRoots']=[*settings['pakRoots'],str(mod)]
            export(wb,settings,profile,output,recipe_path,slug,args.batch_size)
        finally:
            original_error=sys.exc_info()[1]
            try:
                if started:
                    helper.stop(wb,resource)
                if launched_pid is not None:
                    instances=wb.tool('workbench_status',{})['workbench']['instances']
                    if any(i['processId']==launched_pid for i in instances):
                        wb.process=launched_pid
                        helper.close_project(wb,'Enfusion Workbench - '+project.parent.name)
                if previous_project:
                    restored=wb.tool('workbench_open_project',{'projectPath':str(previous_project)})
                    helper.wait_project(wb,previous_project,restored['processId'])
            except Exception as cleanup:
                if original_error is None: raise
                print(f'Workbench cleanup requires attention: {cleanup}',file=sys.stderr)
    else:
        instances=wb.tool('workbench_status',{})['workbench']['instances']
        if len(instances)!=1: raise SystemExit('Keep exactly one Workbench instance open for this export.')
        wb.process=instances[0]['processId']
        if recipe['worldSuffix'].lower() not in wb.call('GC_TestPlaybackMapInfo').lower(): raise SystemExit("Open the recipe's test world and start Play first.")
        export(wb,settings,profile,output,recipe_path,slug,args.batch_size)
        print('Stop Play when finished.')
    print('Ready to upload in Administration > Playback maps:\n'+str(output/f'{slug}.zip'))


def export(wb,settings,profile,output,recipe_path,slug,batch_size):
    process=wb.process
    extracted=subprocess.check_output(['dotnet',str(ROOT/'src/GC.MapExtract/bin/Release/net10.0/GC.MapExtract.dll'),str(recipe_path),str(output/'source-cache'),*settings['pakRoots']],cwd=ROOT,text=True).strip().splitlines()[-1]
    run([settings.get('node','node'),ROOT/'scripts/decode-map-package.mjs',extracted,output/'decoded'])
    base=[sys.executable,ROOT/'scripts/workbench-map-capture.py','--configuration',settings['mcpConfiguration'],'--process',process,'--profile',profile,'--map',slug]
    run(base)
    print('Checking installed asset content for shared building previews...',flush=True)
    load('map-previews.py').configure_library(profile,slug,list(dict.fromkeys([*settings.get('previewSourceRoots',[]),*settings['pakRoots']])))
    build=[sys.executable,ROOT/'scripts/build-workbench-map.py','--sources',extracted,'--decoded',output/'decoded','--profile',profile,'--output',output]
    run([*build,'--phase','terrain'])
    run([*base,'--resume','--phase','water','--input',output/'water-probes.tsv','--output',output/'water-depth.tsv'])
    run([*base,'--resume','--phase','photos','--batch-size',batch_size,'--input',output/f'artifacts/map-tools/{slug}-building-capture-queue.json'])
    run([*build,'--phase','finish'])

if __name__=='__main__': main()
