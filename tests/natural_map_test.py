import importlib.util, unittest
from pathlib import Path
import numpy as np
import tempfile
from PIL import Image
spec=importlib.util.spec_from_file_location('natural',Path(__file__).parents[1]/'scripts/build-arland-natural.py')
natural=importlib.util.module_from_spec(spec);spec.loader.exec_module(natural)
class NaturalMapTests(unittest.TestCase):
    def test_submerged_scenery_fades_by_top_depth(self):
        exposed=dict(bottom=-2,top=3,submergedDepth=0)
        self.assertEqual(natural.water_visibility(exposed),1)
        shallow=dict(bottom=-2,top=-.2,submergedDepth=.2)
        deep=dict(bottom=-8,top=-6,submergedDepth=6)
        self.assertGreater(natural.water_visibility(shallow),.9)
        self.assertLess(natural.water_visibility(deep),.05)
        with self.assertRaises(ValueError):
            natural.water_visibility(dict(bottom=2,top=1,submergedDepth=0))
        with self.assertRaises(KeyError):
            natural.water_visibility({})

    def test_overlap_accumulates_without_becoming_solid(self):
        # Use a tiny image in the same world-coordinate transform.
        original=natural.SIZE;natural.SIZE=16
        try:
            tau=np.zeros((16,16),dtype=np.float32)
            natural.stamp(tau,2048,2048,768,768,.3)
            once=1-np.exp(-tau[8,8])
            natural.stamp(tau,2048,2048,768,768,.3)
            twice=1-np.exp(-tau[8,8])
            self.assertAlmostEqual(float(once),.3,places=6)
            self.assertAlmostEqual(float(twice),.51,places=6)
            self.assertEqual(tau[0,0],0)
        finally: natural.SIZE=original
    def test_north_is_top(self):
        original=natural.SIZE;natural.SIZE=16
        try:
            tau=np.zeros((16,16),dtype=np.float32)
            natural.stamp(tau,2048,3584,256,256,.3)
            self.assertGreater(tau[2,8],0)
            self.assertEqual(tau[14,8],0)
        finally: natural.SIZE=original
    def test_ground_preserves_source_color_without_baking_a_water_seam(self):
        original=natural.SIZE; natural.SIZE=16
        try:
            with tempfile.TemporaryDirectory() as folder:
                ground=Path(folder)/'ground.png';water=Path(folder)/'water.png'
                Image.new('RGB',(16,16),(104,121,139)).save(ground)
                Image.new('RGB',(16,16),(83,160,174)).save(water)
                grid=dict(rows=513,columns=513,spacing=8,heights=[10.]*(513*513))
                self.assertEqual(natural.relief(grid,ground).getpixel((8,8)),(104,121,139))
                grid['heights']=[-1.]*(513*513)
                self.assertEqual(natural.relief(grid,ground).getpixel((8,8)),(104,121,139))
        finally:natural.SIZE=original
    def test_categories(self):
        self.assertEqual(natural.group(100),'rocks')
        for kind in (1,7,8,9): self.assertEqual(natural.group(kind),'bushes')
        for kind in (2,3,5,6): self.assertEqual(natural.group(kind),'trees')
    def test_water_palette_is_continuous_and_monotonically_darker(self):
        colors=natural._water.water_colors(np.linspace(0,150,15001))
        self.assertTrue(np.all(np.diff(colors,axis=0)<=0))
        self.assertLess(float(abs(np.diff(colors,axis=0)).max()),.3)
        np.testing.assert_array_equal(colors[-1],colors[-2])
if __name__=='__main__':unittest.main()
