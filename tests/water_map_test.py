import importlib.util, json, tempfile, unittest
from pathlib import Path
import numpy as np
from PIL import Image

spec=importlib.util.spec_from_file_location('water',Path(__file__).parents[1]/'scripts/build-arland-water.py')
water=importlib.util.module_from_spec(spec);spec.loader.exec_module(water)

class WaterMapTests(unittest.TestCase):
    def test_vertex_grid_maps_to_pixel_centres_without_half_cell_shift(self):
        heights=np.tile(np.arange(513,dtype=np.float32)*8,(513,1))
        result=water.palette.terrain_pixels(heights,4096)
        np.testing.assert_allclose(result[100,100:3900],np.arange(100,3900)+.5,atol=.8)
        north=water.palette.terrain_pixels(heights.T,4096)
        np.testing.assert_allclose(north[100:3900,100],4095.5-np.arange(100,3900),atol=.8)
    def test_inland_world_below_zero_has_no_implicit_ocean(self):
        old_size=water.SIZE
        water.SIZE=16
        try:
            with tempfile.TemporaryDirectory() as folder:
                root=Path(folder)
                (root/'detail.json').write_text('{"features":[]}')
                (root/'height.json').write_text(json.dumps(dict(rows=3,columns=3,spacing=8,heights=[-5]*9)))
                (root/'depth.tsv').write_text('x\tz\tground\tsurface\ttype\twet\n')
                water.build(root/'detail.json',root/'height.json',root/'depth.tsv',root/'water.webp',16,'test',None)
                self.assertFalse(np.asarray(Image.open(root/'water.webp'))[:,:,3].any())
                self.assertEqual(json.loads((root/'water.json').read_text())['depthRange'],[])
        finally:
            water.SIZE=old_size

    def test_banks_depth_and_ocean_transition(self):
        previous=water.SIZE;water.SIZE=16
        try:
            with tempfile.TemporaryDirectory() as folder:
                p=Path(folder);detail=p/'details.json';height=p/'height.json';samples=p/'samples.tsv';output=p/'out.webp'
                detail.write_text(json.dumps({'features':[{'layer':'water','points':[0,0,4096,0,4096,4096,0,4096]}]}))
                height.write_text(json.dumps(dict(rows=513,columns=513,spacing=8,heights=[-.5]*(513*513))))
                rows=['x\tz\tground\tsurface\ttype\twet\n']
                for y in range(16):
                    for x in range(16):
                        ground,surface,kind,wet=(3,0,0,0) if x<7 else (3,2.5,2,1) if x==7 else (1,3,2,1) if x<12 else (-2,0,1,1)
                        rows.append(f'{(x+.5)*256}\t{(15.5-y)*256}\t{ground}\t{surface}\t{kind}\t{wet}\n')
                samples.write_text(''.join(rows));water.build(detail,height,samples,output)
                rgba=np.asarray(Image.open(output))
                self.assertEqual(rgba[8,0,3],0) # dry ground outside the narrow softened bank
                self.assertEqual(rgba[8,15,3],255)
                self.assertLess(rgba[8,7,3],128) # bank at water height is excluded before smoothing
                self.assertGreater(rgba[8,8,3],128)
                self.assertTrue(np.all(rgba[8,9,:3]<rgba[8,15,:3])) # deeper pond is darker
                np.testing.assert_allclose(rgba[8,15,:3],np.rint(water.palette.water_colors(.5)),atol=1)
                samples.write_text(''.join(rows[:-1]))
                with self.assertRaisesRegex(ValueError,'Incomplete'):water.build(detail,height,samples,output)
        finally:water.SIZE=previous

if __name__=='__main__':unittest.main()
