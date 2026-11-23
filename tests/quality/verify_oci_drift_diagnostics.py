#!/usr/bin/env python3
"""Assert fail-closed OCI pin drift prints independently checked MCR indexes."""
import sys
from pathlib import Path

GATE = """      - name: Test and validate fail-closed .NET OCI manifest index pin
        run: |
          python3 -m unittest discover -s tests/quality -p 'test_dotnet_image_pins.py'
          python3 tests/quality/verify_dotnet_image_pins.py --offline
"""
REMOTE = "          python3 tests/quality/verify_dotnet_image_pins.py --remote\n"
DIAGNOSTIC = """      - name: Read-only MCR index discovery after failing OCI pin gate (#212)
        if: failure()
        run: python3 tests/quality/verify_dotnet_image_pins.py --discover
"""
NEXT = '      - name: Test and validate pinned .NET toolchain contract\n'


def validate_ci(content: str) -> None:
    # Ignore explanatory comments while validating executable YAML ordering.
    content = ''.join(line for line in content.splitlines(keepends=True)
                      if not line.lstrip().startswith('#'))
    if GATE + REMOTE + DIAGNOSTIC + NEXT not in content:
        raise ValueError('OCI diagnostic must directly follow unmodified blocking remote pin gate')
    window = content[content.index(GATE):content.index(NEXT, content.index(GATE))]
    if 'continue-on-error:' in window or 'if: false' in window or 'exit 0' in window:
        raise ValueError('OCI gate or diagnostic must not suppress failure')


def verify(root: Path) -> None:
    path = root / '.github/workflows/ci.yml'
    try:
        validate_ci(path.read_text(encoding='utf-8'))
    except OSError as exc:
        raise ValueError('missing CI workflow') from exc

if __name__ == '__main__':
    try:
        verify(Path(__file__).resolve().parents[2])
    except ValueError as exc:
        print('OCI_DIAGNOSTIC_FAILED:', exc, file=sys.stderr)
        sys.exit(1)
    print('OCI_DIAGNOSTIC_OK: pin drift remains blocking; read-only discovery follows')
