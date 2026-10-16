import contextlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


source = Path(__file__).resolve().parents[1] / "scripts" / "lint_ui.py"
spec = importlib.util.spec_from_file_location("lint_ui", source)
lint = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lint)


class UiLintTests(unittest.TestCase):
    def setUp(self):
        lint.failures.clear()

    def test_actual_project_has_binding_coverage(self):
        with contextlib.redirect_stdout(io.StringIO()):
            count = lint.check_bindings()
        self.assertGreater(count, 0)
        self.assertEqual(lint.failures, [])

    def test_empty_project_fails(self):
        with tempfile.TemporaryDirectory() as empty, patch.object(lint, "APP", empty):
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(lint.main(), 1)

    def test_missing_target_fails(self):
        with patch.object(lint, "BOUND_XAML", ["Views/DoesNotExist.xaml"]):
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(lint.main(), 1)
        self.assertTrue(any("目标缺失" in failure for failure in lint.failures))

    def test_unknown_binding_is_detected(self):
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "Broken.xaml").write_text('<TextBlock Text="{Binding MissingRegressionProperty}"/>')
            with patch.object(lint, "APP", directory), patch.object(lint, "BOUND_XAML", ["Broken.xaml"]):
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(lint.main(), 1)
        self.assertTrue(any("MissingRegressionProperty" in failure for failure in lint.failures))


if __name__ == "__main__":
    unittest.main()
