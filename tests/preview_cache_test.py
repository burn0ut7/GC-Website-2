import importlib.util, os, tempfile, unittest
from PIL import Image
from pathlib import Path

spec=importlib.util.spec_from_file_location('previews',Path(__file__).parents[1]/'scripts/map-previews.py')
previews=importlib.util.module_from_spec(spec);spec.loader.exec_module(previews)

class PreviewCacheTests(unittest.TestCase):
    def test_interrupted_capture_is_not_promoted(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);assets=root/'assets';assets.mkdir();(assets/'data.pak').write_bytes(b'a')
            (root/'GC_MapExport_test.json').write_text('{}');previews.configure_library(root,'test',[assets])
            receipt=root/'GC_BuildingPhoto_0.resource';receipt.write_text('{guid}Prefab.et')
            image=receipt.with_suffix('.png');Image.new('RGB',(2,2)).save(image)
            image.write_bytes(image.read_bytes()[:25])
            previews.preserve(root,'test')
            self.assertEqual(previews.available(root,'test'),{})
            with self.assertRaises(ValueError): previews.preserve_capture(root,'test',receipt)

    def test_shared_across_maps_and_invalidated_by_source_content(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);assets=root/'assets';assets.mkdir();pak=assets/'data.pak';pak.write_bytes(b'first')
            for name in ('one','two'):
                (root/f'GC_MapExport_{name}.json').write_text('{}')
            previews.configure_library(root,'one',[assets])
            receipt=root/'GC_BuildingPhoto_0.resource';receipt.write_text('{guid}Prefab.et');Image.new('RGB',(2,2)).save(receipt.with_suffix('.png'))
            previews.preserve(root,'one');receipt.unlink();receipt.with_suffix('.png').unlink()
            previews.configure_library(root,'two',[assets])
            self.assertEqual(len(previews.available(root,'two')),1)
            pak.write_bytes(b'other')
            previews.configure_library(root,'two',[assets])
            self.assertEqual(previews.available(root,'two'),{})
            previews.configure_library(root,'one',[assets])
            previews.preserve(root,'one')
            self.assertEqual(previews.available(root,'one'),{})

    def test_reuse_is_only_within_the_same_export_session(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);marker=root/'GC_MapExport_test.json';receipt=root/'GC_BuildingPhoto_0.resource';image=receipt.with_suffix('.png')
            marker.write_text('{"world":"test"}');receipt.write_text('{guid}Prefab.et');Image.new('RGB',(2,2)).save(image)
            os.utime(marker,ns=(1000000000,1000000000));os.utime(receipt,ns=(2000000000,2000000000));os.utime(image,ns=(3000000000,3000000000))
            self.assertEqual(len(previews.available(root,'test')),1)
            previews.preserve(root,'test');receipt.unlink();image.unlink()
            self.assertEqual(len(previews.available(root,'test')),1)
            # Same prefab path in a new export must be captured again.
            os.utime(marker,ns=(4000000000,4000000000))
            self.assertEqual(previews.available(root,'test'),{})

if __name__=='__main__': unittest.main()
