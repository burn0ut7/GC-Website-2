import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('export_map', ROOT/'scripts/export-map.py')
export_map = importlib.util.module_from_spec(spec)
spec.loader.exec_module(export_map)


class WorkshopExportTests(unittest.TestCase):
    def test_failed_world_open_closes_owned_process_and_verifies_restore(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            settings = root/'settings.json'
            settings.write_text(json.dumps(dict(profile=directory, output=directory, mcpConfiguration='fixture')))
            recipe = root/'recipe.json'
            recipe.write_text(json.dumps(dict(slug='test', workshopId='test', parentWorld='test')))
            helper, capture = Mock(), Mock()
            wb = capture.Workbench.return_value
            original = root/'original/addon.gproj'
            project = root/'GCMapExporttest/addon.gproj'
            helper.prepare.return_value = project, '$project:worlds/test.ent', root
            helper.release_idle_terrain.return_value = original
            helper.start.side_effect = ValueError('world open failed')
            wb.tool.side_effect = [
                {'workbench': {'instances': []}}, {'processId': 101},
                {'workbench': {'instances': [{'processId':101}]}}, {'processId': 102},
            ]
            with patch.object(export_map, 'run'), patch.object(export_map, 'load', side_effect=[helper,capture]), \
                 patch('sys.argv', ['export-map.py','--settings',str(settings),'--recipe',str(recipe)]):
                with self.assertRaisesRegex(ValueError, 'world open failed'):
                    export_map.main()
            helper.close_project.assert_called_once_with(wb,'Enfusion Workbench - GCMapExporttest')
            self.assertEqual(helper.wait_project.call_args_list[-1].args,(wb,original,102))
            helper.stop.assert_not_called()
