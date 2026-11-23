"""Behavioral RED fixtures: no fake security clean bill on missing or high advisories."""
import unittest
from datetime import date

from supply_chain_audit import (
    parse_npm, parse_nuget, assess_findings, validate_scanner_result
)

ADVISORY = "https://github.com/advisories/GHSA-aaaa-bbbb-cccc"
TODAY = date(2026, 11, 23)


def npm_report(severity="high"):
    return {
        "auditReportVersion": 2,
        "vulnerabilities": {
            "example": {
                "name": "example", "severity": severity,
                "isDirect": False, "range": "<2.0.0",
                "via": [{"source": 12345, "name": "example",
                         "severity": severity, "url": ADVISORY}],
                "effects": [], "nodes": ["node_modules/example"],
                "fixAvailable": True
            }
        },
        "metadata": {"vulnerabilities": {"info": int(severity == "info"),
                                        "low": int(severity == "low"),
                                        "moderate": int(severity == "moderate"),
                                        "high": int(severity == "high"),
                                        "critical": int(severity == "critical"),
                                        "total": 1},
                     "dependencies": {"total": 1}}
    }


def nuget_report(severity="High"):
    return {
        "version": 1, "parameters": "--vulnerable --include-transitive",
        "sources": ["https://api.nuget.org/v3/index.json"],
        "projects": [{
            "path": "/repo/tests/Example/Example.csproj",
            "frameworks": [{
                "framework": "net10.0",
                "topLevelPackages": [],
                "transitivePackages": [{
                    "id": "Example.Package", "resolvedVersion": "1.0.0",
                    "vulnerabilities": [{"severity": severity,
                                         "advisoryurl": ADVISORY}]
                }]
            }]
        }]
    }


class SupplyChainSecurityContractTests(unittest.TestCase):
    def test_high_npm_transitive_advisory_is_blocked(self):
        findings = parse_npm(npm_report(), {"example": {"1.0.0"}})
        self.assertEqual(len(findings), 1)
        self.assertEqual(findings[0]["severity"], "high")
        self.assertEqual(findings[0]["version"], "1.0.0")
        self.assertEqual(len(assess_findings(findings, [], TODAY)["blocked"]), 1)

    def test_nuget_transitive_critical_is_blocked(self):
        findings = parse_nuget(nuget_report("Critical"),
                               "tests/Example/Example.csproj",
                               {"example.package": {"1.0.0"}})
        self.assertEqual(len(findings), 1)
        self.assertEqual(findings[0]["severity"], "critical")
        self.assertEqual(len(assess_findings(findings, [], TODAY)["blocked"]), 1)

    def test_low_and_moderate_are_reported_not_silently_dropped(self):
        npm = parse_npm(npm_report("moderate"), {"example": {"1.0.0"}})
        dotnet = parse_nuget(nuget_report("Low"),
                            "tests/Example/Example.csproj",
                            {"example.package": {"1.0.0"}})
        result = assess_findings(npm + dotnet, [], TODAY)
        self.assertEqual(len(result["warnings"]), 2)
        self.assertEqual(result["blocked"], [])

    def test_offline_malformed_and_error_reports_never_mean_clean(self):
        for x in (None, {}, {"error": {"code": "ENOAUDIT"}},
                  {"auditReportVersion": 2, "vulnerabilities": {}}):
            with self.subTest(report=x), self.assertRaises(ValueError):
                parse_npm(x, {})
        with self.assertRaises(ValueError):
            parse_nuget({"version": 1, "projects": []},
                        "tests/Example/Example.csproj", {})
        with self.assertRaises(ValueError):
            validate_scanner_result(2, {"error": "server down"}, "npm")
        with self.assertRaises(ValueError):
            validate_scanner_result(1, {"version": 1}, "nuget")

    def test_exceptions_must_be_exact_documented_and_not_expired(self):
        item = parse_npm(npm_report(), {"example": {"1.0.0"}})[0]
        exemption = {
            "ecosystem": "npm", "package": "example",
            "version": "1.0.0", "advisory": ADVISORY,
            "owner": "security-team", "issue":
                "https://github.com/KeyserDSoze/Omnia/issues/193",
            "reason": "Temporary reviewed mitigation and tracked package upgrade.",
            "expires": "2026-12-01"
        }
        self.assertEqual(assess_findings([item], [exemption], TODAY)["blocked"], [])
        with self.assertRaises(ValueError):
            assess_findings([item], [{**exemption, "expires": "2026-11-22"}], TODAY)
        with self.assertRaises(ValueError):
            assess_findings([item], [{**exemption, "package": "*"}], TODAY)
        with self.assertRaises(ValueError):
            assess_findings([], [exemption], TODAY)

    def test_clean_nuget_project_without_frameworks_is_valid_only_with_audit_sources(self):
        report = {"version": 1, "parameters": "--vulnerable --include-transitive",
                  "sources": ["https://api.nuget.org/v3/index.json"],
                  "projects": [{"path": "/repo/tests/Example/Example.csproj"}]}
        self.assertEqual(parse_nuget(report, "tests/Example/Example.csproj",
                                     {"example.package": {"1.0.0"}}), [])
        report.pop("sources")
        with self.assertRaisesRegex(ValueError, "source"):
            parse_nuget(report, "tests/Example/Example.csproj",
                        {"example.package": {"1.0.0"}})

    def test_npm_indirect_high_without_advisory_is_not_waivable(self):
        report = npm_report()
        report["vulnerabilities"]["example"]["via"] = ["meta-vulnerable-parent"]
        findings = parse_npm(report, {"example": {"1.0.0"}})
        self.assertEqual(len(assess_findings(findings, [], TODAY)["blocked"]), 1)


if __name__ == "__main__":
    unittest.main()
