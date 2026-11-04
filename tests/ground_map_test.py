import importlib.util
from pathlib import Path
import unittest
import numpy as np
from PIL import Image

spec = importlib.util.spec_from_file_location('ground', Path(__file__).parents[1]/'scripts/build-arland-ground.py')
ground = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ground)

class GroundMapTests(unittest.TestCase):
    def test_border_is_removed_before_scaling(self):
        interior = np.zeros((248,248,3),dtype=np.uint8)
        interior[:,:,0] = np.arange(248)
        padded = np.pad(interior,((4,4),(4,4),(0,0)),mode='edge')
        result = ground.tile_interior(Image.fromarray(padded))
        expected = Image.fromarray(interior).resize((256,256),Image.Resampling.BILINEAR)
        np.testing.assert_array_equal(np.asarray(result),np.asarray(expected))

    def test_128_pixel_custom_map_tile(self):
        interior = np.zeros((120,120,3),dtype=np.uint8)
        interior[:,:,1] = np.arange(120)
        padded = np.pad(interior,((4,4),(4,4),(0,0)),mode='edge')
        result = ground.tile_interior(Image.fromarray(padded))
        expected = Image.fromarray(interior).resize((256,256),Image.Resampling.BILINEAR)
        np.testing.assert_array_equal(np.asarray(result),np.asarray(expected))

    def test_128_pixel_compressed_border_tolerance(self):
        pixels = np.full((128,128,3),100,dtype=np.uint8)
        pixels[:4:2] += 1
        result = ground.tile_interior(Image.fromarray(pixels), .25)
        self.assertTrue(np.all(np.asarray(result) == 100))

    def test_unrecognized_border_layout_is_rejected(self):
        noise = np.random.default_rng(7).integers(0,255,(256,256,3),dtype=np.uint8)
        with self.assertRaises(ValueError): ground.tile_interior(Image.fromarray(noise))

    def test_averaging_is_linear_light_not_dark_srgb_mean(self):
        checker = np.indices((16,16)).sum(axis=0)%2*255
        rgb = np.repeat(checker[:,:,None],3,axis=2).astype(np.uint8)
        result = np.asarray(ground.average_ground(Image.fromarray(rgb)))
        self.assertTrue(np.all(result==188))

    def test_actual_solid_color_is_preserved(self):
        result=ground.average_ground(Image.new('RGB',(16,16),(104,121,139)))
        self.assertEqual(result.getpixel((8,8)),(104,121,139))

if __name__=='__main__': unittest.main()
