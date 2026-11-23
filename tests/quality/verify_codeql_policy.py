#!/usr/bin/env python3
"""Fail closed on drift from the reviewed CodeQL GitHub Actions recipe.

This intentionally pins the complete small workflow, not a partial regex
match against untrusted YAML. A change must be reviewed alongside this policy.
Actual GitHub CodeQL execution and uploaded SARIF are separate required proof.
"""
import sys
from pathlib import Path

EXPECTED_WORKFLOW = "name: Omnia CodeQL static security\non:\n  push:\n    branches: [main]\n  pull_request:\n    branches: [main]\n  schedule:\n    - cron: '17 4 * * 2'\n  workflow_dispatch:\npermissions:\n  contents: read\njobs:\n  analyze:\n    name: CodeQL (${{ matrix.language }})\n    runs-on: ubuntu-24.04\n    permissions:\n      contents: read\n      actions: read\n      security-events: write\n    strategy:\n      fail-fast: false\n      matrix:\n        language: [actions, csharp, javascript-typescript, python]\n    steps:\n      - uses: actions/checkout@v4\n      - name: Initialize CodeQL\n        uses: github/codeql-action/init@v4\n        with:\n          languages: ${{ matrix.language }}\n          build-mode: none\n          queries: security-extended\n      - name: Analyze and upload CodeQL results\n        uses: github/codeql-action/analyze@v4\n        with:\n          category: /language:${{ matrix.language }}\n"


def validate_workflow(source: str) -> None:
    if not isinstance(source, str) or source != EXPECTED_WORKFLOW:
        raise ValueError("CodeQL workflow drift: languages, matrix, events, permissions, "
                         "build mode, security queries or allowed actions changed")


def verify(root: Path) -> None:
    path = root / '.github/workflows/codeql.yml'
    try:
        content = path.read_text(encoding='utf-8')
    except (OSError, UnicodeError) as exc:
        raise ValueError("CodeQL workflow missing/unreadable") from exc
    validate_workflow(content)


if __name__ == '__main__':
    try:
        verify(Path(__file__).resolve().parents[2])
    except ValueError as exc:
        print(f'CODEQL_POLICY_FAILED: {exc}', file=sys.stderr)
        sys.exit(1)
    print('CODEQL_POLICY_OK: reviewed full workflow recipe; real CodeQL run still required')
