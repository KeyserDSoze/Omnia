"""Fail-closed advisory policy for npm and NuGet (TDD RED skeleton)."""
from datetime import date


def parse_npm(report, locked):
    return []


def parse_nuget(report, expected_project, locked):
    return []


def assess_findings(findings, exceptions, today: date):
    return {"blocked": [], "accepted": [], "warnings": []}


def validate_scanner_result(exit_code, report, ecosystem):
    return None
