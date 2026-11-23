"""Regression contract for a read-only index discovery that cannot mask red CI."""
import unittest
from pathlib import Path
from verify_oci_drift_diagnostics import validate_ci, verify

PREFIX = '''      - name: Test and validate fail-closed .NET OCI manifest index pin
        run: |
          python3 -m unittest discover -s tests/quality -p 'test_dotnet_image_pins.py'
          python3 tests/quality/verify_dotnet_image_pins.py --offline
          python3 tests/quality/verify_dotnet_image_pins.py --remote
'''
DIAG = '''      - name: Read-only MCR index discovery after failing OCI pin gate (#212)
        if: failure()
        run: python3 tests/quality/verify_dotnet_image_pins.py --discover
'''
NEXT = '''      - name: Test and validate pinned .NET toolchain contract
        run: |
          python3 tests/quality/verify_dotnet_toolchain.py
'''


class OciDriftDiagnosticsTests(unittest.TestCase):
    def test_actual_ci_has_failure_only_discovery(self):
        verify(Path(__file__).resolve().parents[2])

    def test_valid_fixture_keeps_original_remote_gate_intact(self):
        validate_ci(PREFIX + DIAG + NEXT)

    def test_missing_discovery_must_fail(self):
        with self.assertRaisesRegex(ValueError, 'diagnostic'):
            validate_ci(PREFIX + NEXT)

    def test_diagnostic_must_run_only_on_failure_not_always(self):
        with self.assertRaises(ValueError):
            validate_ci(PREFIX + DIAG.replace('if: failure()', 'if: always()') + NEXT)

    def test_remote_check_cannot_be_bypassed(self):
        with self.assertRaises(ValueError):
            validate_ci(PREFIX.replace(' --remote', ' --offline') + DIAG + NEXT)

    def test_no_silent_success_or_skip(self):
        for change in ('        continue-on-error: true\n', '        if: false\n'):
            with self.subTest(change=change):
                with self.assertRaises(ValueError):
                    validate_ci((PREFIX + DIAG + NEXT).replace(PREFIX, PREFIX + change))

    def test_discovery_must_be_directly_after_pin_validation(self):
        with self.assertRaises(ValueError):
            validate_ci(DIAG + PREFIX + NEXT)

if __name__ == '__main__':
    unittest.main()
