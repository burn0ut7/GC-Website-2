import importlib.util,json,tempfile,unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('buildings',Path(__file__).parents[1]/'scripts/build-arland-buildings.py')
buildings=importlib.util.module_from_spec(spec);spec.loader.exec_module(buildings)

class BuildingCatalogTests(unittest.TestCase):
    def test_arbitrary_wall_asset_binds_rendered_geometry_and_reports_missing(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);profile=root/'profile';profile.mkdir()
            (root/'dist/maps').mkdir(parents=True);(root/'artifacts/map-tools').mkdir(parents=True)
            (profile/'GC_MapExport_test.json').write_text('{}')
            (profile/'GC_MapBuildings_test.tsv').write_text('minX\tminZ\tmaxX\tmaxZ\tresource\n0\t0\t10\t1\t{custom}UnknownMod/Anything.et\n20\t20\t21\t21\t{other}Other.et\n')
            features=[{'layer':'walls','kind':2,'points':[0,0,10,0,10,1,0,1]},
                      {'layer':'buildings','kind':0,'points':[30,30,31,30,31,31,30,31]}]
            for name in ('test-detail.json','test-detail-0.json'):
                (root/'dist/maps'/name).write_text(json.dumps({'version':1,'features':features}))
            buildings.build(profile,root,'test','test')
            catalog=json.loads((root/'dist/maps/test-buildings.json').read_text())
            for name in ('test-detail.json','test-detail-0.json'):
                result=json.loads((root/'dist/maps'/name).read_text())['features']
                self.assertEqual(catalog['models'][result[0]['model']]['resource'],'{custom}UnknownMod/Anything.et')
                self.assertNotIn('model',result[1])
            self.assertEqual(catalog['coverage'],{'matched':1,'unresolved':1})
            buildings.finish_previews(profile,root,'test')
            self.assertEqual(json.loads((root/'dist/maps/test-buildings.json').read_text()),catalog)
            with (profile/'GC_MapBuildings_test.tsv').open('a') as source:
                source.write('stale')
            with self.assertRaisesRegex(ValueError,'changed since matching'):
                buildings.finish_previews(profile,root,'test')


    def test_duplicate_instances_and_facility_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);profile=root/'profile';profile.mkdir()
            (root/'dist/maps').mkdir(parents=True);(root/'artifacts/map-tools').mkdir(parents=True)
            (profile/'GC_MapExport_test.json').write_text('{}')
            (profile/'GC_MapBuildings_test.tsv').write_text('minX\tminZ\tmaxX\tmaxZ\tresource\n0\t0\t10\t10\t{a}house.et\n0\t0\t10\t10\t{a}house.et\n')
            (profile/'GC_MapSymbols_test.tsv').write_text('type\tx\tz\tyaw\tresource\nMDT_CRANE\t20\t30\t0\t{b}crane.et\n')
            (root/'dist/maps/test-detail.json').write_text(json.dumps({'features':[{'layer':'buildings','points':[0,0,10,0,10,10,0,10]}]}))
            (root/'dist/maps/test-symbols.json').write_text(json.dumps({'version':1,'styles':{},'symbols':[{'type':'MDT_CRANE','x':20,'z':30,'yaw':0}]}))
            buildings.build(profile,root,'test','test')
            catalog=json.loads((root/'dist/maps/test-buildings.json').read_text())
            symbols=json.loads((root/'dist/maps/test-symbols.json').read_text())
            detail=json.loads((root/'dist/maps/test-detail.json').read_text())
            self.assertIn('model',detail['features'][0])
            self.assertEqual(catalog['models'][symbols['symbols'][0]['model']]['resource'],'{b}crane.et')

    def test_overlapping_instances_form_one_preview(self):
        for resources in (('house.et','house.et'),('shell.et','interior.et')):
            with self.subTest(resources=resources), tempfile.TemporaryDirectory() as directory:
                root=Path(directory);profile=root/'profile';profile.mkdir()
                (root/'dist/maps').mkdir(parents=True);(root/'artifacts/map-tools').mkdir(parents=True)
                (profile/'GC_MapExport_test.json').write_text('{}')
                rows=['minX\tminZ\tmaxX\tmaxZ\tresource\tt0\tt1\tt2\tt3']
                for i,resource in enumerate(resources):
                    rows.append(f'{i}\t0\t{10+i}\t10\t{resource}\t1 0 0\t0 1 0\t0 0 1\t{i} 0 0')
                (profile/'GC_MapBuildings_test.tsv').write_text('\n'.join(rows)+'\n')
                (root/'dist/maps/test-detail.json').write_text(json.dumps({'features':[{'layer':'buildings','points':[0,0,10,0,10,10,0,10]}]}))
                buildings.build(profile,root,'test','test')
                feature=json.loads((root/'dist/maps/test-detail.json').read_text())['features'][0]
                queue=json.loads((root/'artifacts/map-tools/test-building-capture-queue.json').read_text())
                self.assertNotIn('models',feature)
                self.assertEqual(len(queue),1)
                self.assertTrue(queue[0]['resource'].startswith('assembly:'))
                self.assertEqual(len(queue[0]['members']),2)

if __name__=='__main__':unittest.main()
