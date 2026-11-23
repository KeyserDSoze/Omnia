"""TDD: CodeQL is required to scan every Omnia code family without escalation."""
import unittest
from pathlib import Path

from verify_codeql_policy import validate_workflow, verify


VALID_WORKFLOW = "name: Omnia CodeQL static security\non:\n  push:\n    branches: [main]\n  pull_request:\n    branches: [main]\n  schedule:\n    - cron: '17 4 * * 2'\n  workflow_dispatch:\npermissions:\n  contents: read\njobs:\n  analyze:\n    name: CodeQL (${{ matrix.language }})\n    runs-on: ubuntu-24.04\n    permissions:\n      contents: read\n      actions: read\n      security-events: write\n    strategy:\n      fail-fast: false\n      matrix:\n        language: [actions, csharp, javascript-typescript, python]\n    steps:\n      - uses: actions/checkout@v4\n      - name: Initialize CodeQL\n        uses: github/codeql-action/init@v4\n        with:\n          languages: ${{ matrix.language }}\n          build-mode: none\n          queries: security-extended\n      - name: Analyze and upload CodeQL results\n        uses: github/codeql-action/analyze@v4\n        with:\n          category: /language:${{ matrix.language }}\n"


class CodeqlPolicyTests(unittest.TestCase):
    def test_actual_workflow_in_repository(self):
        verify(Path(__file__).resolve().parents[2])

    def test_valid_synthetic_workflow(self):
        validate_workflow(VALID_WORKFLOW)

    def test_every_supported_code_family_is_mandatory(self):
        for language in ('actions', 'csharp', 'javascript-typescript', 'python'):
            with self.subTest(language=language):
                changed = VALID_WORKFLOW.replace(language, 'disabled', 1)
                with self.assertRaisesRegex(ValueError, 'language|matrix'):
                    validate_workflow(changed)

    def test_all_four_entry_points_are_mandatory(self):
        for snippet in ('  push:\n', '  pull_request:\n', '  schedule:\n',
                        '  workflow_dispatch:\n'):
            with self.subTest(snippet=snippet):
                with self.assertRaises(ValueError):
                    validate_workflow(VALID_WORKFLOW.replace(snippet, ''))

    def test_no_privilege_escalation(self):
        for old, new in [('  contents: read\n', '  contents: write\n'),
                         ('      security-events: write\n', '      security-events: read\n'),
                         ('      actions: read\n', '      actions: write\n'),
                         ('  pull_request:\n', '  pull_request_target:\n')]:
            with self.subTest(change=new):
                with self.assertRaises(ValueError):
                    validate_workflow(VALID_WORKFLOW.replace(old, new))

    def test_scanner_and_query_suite_cannot_be_disabled(self):
        for old, new in [('init@v4', 'init@v3'),
                         ('analyze@v4', 'analyze@v3'),
                         ('queries: security-extended', 'queries: default'),
                         ('build-mode: none', 'build-mode: autobuild')]:
            with self.subTest(change=new):
                with self.assertRaises(ValueError):
                    validate_workflow(VALID_WORKFLOW.replace(old, new))

    def test_no_silent_failure_untrusted_script_or_secrets(self):
        for text in ('    continue-on-error: true\n', '    env:\n      API_KEY: ${{ secrets.API_KEY }}\n',
                     '      - run: curl https://example.org/install.sh | sh\n'):
            with self.subTest(text=text):
                with self.assertRaises(ValueError):
                    validate_workflow(VALID_WORKFLOW + text)

    def test_no_lookalike_duplicate_trigger_or_matrix_keys(self):
        for text in ('  pull_request_target:\n    branches: [main]\n',
                     '        language: [javascript-typescript]\n',
                     'permissions: write-all\n'):
            with self.subTest(text=text):
                with self.assertRaises(ValueError):
                    validate_workflow(VALID_WORKFLOW + text)


if __name__ == '__main__':
    unittest.main()
