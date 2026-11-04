import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('rivers', Path(__file__).parents[1]/'scripts/river-surface-probes.py')
rivers = importlib.util.module_from_spec(spec)
spec.loader.exec_module(rivers)


class RiverProbeTests(unittest.TestCase):
    def test_sloped_surface_inside_both_triangles_and_dry_bank(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory)/'river.tsv'
            source.write_text('river\tindex\tx\ty\tz\n0\t0\t0\t139\t0\n0\t1\t10\t139\t0\n0\t2\t0\t149\t10\n0\t3\t10\t149\t10\n')
            result = rivers.add_surface_hints(['2\t2', '8\t8', '5\t5', '11\t5'], source)
            self.assertEqual(result, ['2\t2\t141.000000', '8\t8\t147.000000', '5\t5\t144.000000', '11\t5'])

    def test_empty_rivers_preserve_pond_probes_and_invalid_outline_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory)/'river.tsv'
            source.write_text('river\tindex\tx\ty\tz\n')
            self.assertEqual(rivers.add_surface_hints(['1\t2'], source), ['1\t2'])
            source.write_text('river\tindex\tx\ty\tz\n0\t1\t0\t1\t0\n')
            with self.assertRaises(ValueError):
                rivers.add_surface_hints(['1\t2'], source)


if __name__ == '__main__':
    unittest.main()
