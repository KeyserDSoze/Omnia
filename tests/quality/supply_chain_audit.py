#!/usr/bin/env python3
"""Strict, testable advisory policy. No scanner output means NO security pass.

Native npm audit v2 and NuGet package list v1 findings are checked
against the already-locked package graph. No automatic suppressions.
"""
from datetime import date
from urllib.parse import urlparse
import re

SEVERITY = {"info", "low", "moderate", "high", "critical"}
BLOCK = {"high", "critical"}
ISSUE_URL = re.compile(r"https://github\.com/KeyserDSoze/Omnia/issues/[1-9][0-9]*\Z")
VALID_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9._@/-]*\Z")


def _severity(raw):
    if not isinstance(raw, str) or raw.lower() not in SEVERITY:
        raise ValueError(f"invalid advisory severity: {raw!r}")
    return raw.lower()


def _advisory(url):
    if not isinstance(url, str) or not url.startswith("https://"):
        raise ValueError("advisory missing HTTPS URL")
    parsed = urlparse(url)
    if parsed.hostname not in ("github.com", "nvd.nist.gov",
                               "osv.dev", "www.nuget.org", "npmjs.com",
                               "www.npmjs.com"):
        raise ValueError("untrusted advisory authority")
    if parsed.username or parsed.password or not parsed.path.strip("/"):
        raise ValueError("invalid advisory reference")
    return url


def _finding(ecosystem, package, version, severity, advisory, project=None):
    if not isinstance(package, str) or not package or not isinstance(version, str) or not version:
        raise ValueError("audit result missing package name or version")
    finding = {"ecosystem": ecosystem, "package": package.lower(),
               "version": version, "severity": _severity(severity),
               "advisory": advisory}
    if project:
        finding["project"] = project
    return finding


def validate_scanner_result(exit_code, report, ecosystem):
    if ecosystem == "npm":
        if exit_code not in (0, 1):
            raise ValueError(f"npm audit scanner failed (exit {exit_code})")
        if not isinstance(report, dict) or report.get("auditReportVersion") != 2:
            raise ValueError("npm audit output is not v2 JSON")
        if "error" in report or not isinstance(report.get("vulnerabilities"), dict):
            raise ValueError("npm advisory service unavailable or malformed report")
        metadata = report.get("metadata")
        counts = metadata.get("vulnerabilities") if isinstance(metadata, dict) else None
        if not isinstance(counts, dict) or not isinstance(counts.get("total"), int):
            raise ValueError("npm audit missing vulnerability metadata totals")
        if counts["total"] < 0 or counts["total"] != len(report["vulnerabilities"]):
            raise ValueError("npm audit vulnerability counts mismatch")
        for level in ("info", "low", "moderate", "high", "critical"):
            if level not in counts or type(counts[level]) is not int or counts[level] < 0:
                raise ValueError("npm audit missing severity count")
        if sum(counts[x] for x in SEVERITY) != counts["total"]:
            raise ValueError("npm audit vulnerability severity counts mismatch")
    elif ecosystem == "nuget":
        if exit_code != 0:
            raise ValueError(f"NuGet vulnerability scanner failed (exit {exit_code})")
        if not isinstance(report, dict) or report.get("version") != 1:
            raise ValueError("NuGet vulnerability output is not version-1 JSON")
        if "error" in report or not isinstance(report.get("projects"), list):
            raise ValueError("NuGet audit service unavailable or malformed report")
        # NuGet 10 legitimately omits frameworks when there are NO
        # vulnerable packages. Prove that advisory sources and query mode
        # were present instead of inferring a clean report from [].
        sources = report.get("sources")
        if not isinstance(sources, list) or not sources or not all(
                isinstance(s, str) and s.startswith("https://") for s in sources):
            raise ValueError("NuGet vulnerability source list missing")
        params = report.get("parameters")
        if not isinstance(params, str) or "--vulnerable" not in params or "--include-transitive" not in params:
            raise ValueError("NuGet report was not a transitive vulnerability scan")
    else:
        raise ValueError("unknown ecosystem")
    return None


def parse_npm(report, locked):
    validate_scanner_result(0, report, "npm")
    if not isinstance(locked, dict):
        raise ValueError("npm lock graph missing")
    findings = []
    for name, record in sorted(report["vulnerabilities"].items()):
        if not isinstance(record, dict) or record.get("name") != name:
            raise ValueError(f"npm advisory record malformed for {name}")
        locked_versions = locked.get(name.lower())
        if not isinstance(locked_versions, set) or not locked_versions:
            raise ValueError(f"npm advisory package {name} is not in checked-in lock")
        aggregate = _severity(record.get("severity"))
        vias = record.get("via")
        if not isinstance(vias, list) or not vias:
            raise ValueError(f"npm advisory for {name} has no via evidence")
        source_advisories = []
        for via in vias:
            if isinstance(via, dict):
                v_severity = _severity(via.get("severity"))
                v_url = _advisory(via.get("url"))
                source_advisories.append((v_severity, v_url))
            elif isinstance(via, str) and via:
                # npm's meta-vulnerability via a parent may not have
                # its own advisory URL. Keep an unwaivable blocking
                # marker instead of calling that package clean.
                source_advisories.append((aggregate, "npm:indirect:" + name))
            else:
                raise ValueError(f"npm advisory for {name} has invalid via")
        highest = max(({"info":0,"low":1,"moderate":2,"high":3,"critical":4}[s]
                       for s, _ in source_advisories), default=-1)
        if {"info":0,"low":1,"moderate":2,"high":3,"critical":4}[aggregate] > highest:
            source_advisories.append((aggregate, "npm:indirect:" + name))
        for version in sorted(locked_versions):
            for severity, advisory in sorted(set(source_advisories)):
                findings.append(_finding("npm", name, version, severity, advisory))
    return sorted(findings, key=_sort_key)


def parse_nuget(report, expected_project, locked):
    validate_scanner_result(0, report, "nuget")
    if not isinstance(locked, dict):
        raise ValueError("NuGet lock graph missing")
    projects = report["projects"]
    if len(projects) != 1 or not isinstance(projects[0], dict):
        raise ValueError("NuGet scan missing exactly one expected project")
    project = projects[0]
    reported = str(project.get("path", "")).replace("\\", "/")
    if not reported.endswith("/" + expected_project.replace("\\", "/")) and reported != expected_project:
        raise ValueError("NuGet scan report project path mismatch")
    frameworks = project.get("frameworks")
    if frameworks is None:
        # Official dotnet package list --vulnerable JSON output for a
        # project with no vulnerabilities does not contain frameworks.
        frameworks = []
    if not isinstance(frameworks, list):
        raise ValueError("NuGet audit report has malformed framework records")
    findings = []
    for framework in frameworks:
        if not isinstance(framework, dict) or not isinstance(framework.get("framework"), str):
            raise ValueError("NuGet audit framework malformed")
        for group in ("topLevelPackages", "transitivePackages"):
            packages = framework.get(group, [])
            if not isinstance(packages, list):
                raise ValueError(f"NuGet {group} is invalid")
            for package in packages:
                if not isinstance(package, dict):
                    raise ValueError("NuGet package entry invalid")
                name = package.get("id")
                version = package.get("resolvedVersion")
                if not isinstance(name, str) or not isinstance(version, str) or version not in locked.get(name.lower(), set()):
                    raise ValueError(f"NuGet advisory package {name}@{version} not in lock")
                advisories = package.get("vulnerabilities", [])
                if not isinstance(advisories, list):
                    raise ValueError("NuGet vulnerability entry malformed")
                for advisory in advisories:
                    if not isinstance(advisory, dict):
                        raise ValueError("NuGet vulnerability entry malformed")
                    findings.append(_finding("nuget", name, version,
                                             advisory.get("severity"),
                                             _advisory(advisory.get("advisoryurl")),
                                             expected_project))
    # NuGet can return the same package across frameworks; do not silently
    # omit a distinct advisory, but deduplicate identical findings.
    unique = {(f["ecosystem"], f["package"], f["version"], f["severity"],
               f["advisory"], f.get("project", "")): f for f in findings}
    return sorted(unique.values(), key=_sort_key)


def _sort_key(f):
    return (f["ecosystem"], f["package"], f["version"], f["advisory"],
            f["severity"], f.get("project", ""))


def _exception_id(e):
    return (e["ecosystem"], e["package"].lower(), e["version"], e["advisory"])


def assess_findings(findings, exceptions, today: date):
    if not isinstance(exceptions, list):
        raise ValueError("audit exception list must be an array")
    entries = {}
    for waiver in exceptions:
        required = {"ecosystem", "package", "version", "advisory",
                    "owner", "issue", "reason", "expires"}
        if not isinstance(waiver, dict) or set(waiver) != required:
            raise ValueError("audit exception missing required scoped fields")
        if waiver["ecosystem"] not in ("npm", "nuget"):
            raise ValueError("audit exception has invalid ecosystem")
        if not all(isinstance(waiver[k], str) and VALID_ID.fullmatch(waiver[k])
                   for k in ("package", "version", "owner")):
            raise ValueError("audit exception uses wildcard or malformed package/version/owner")
        if not ISSUE_URL.fullmatch(str(waiver["issue"])):
            raise ValueError("audit exception requires tracked Omnia GitHub issue")
        if not isinstance(waiver["reason"], str) or len(waiver["reason"].strip()) < 30:
            raise ValueError("audit exception requires concrete mitigation rationale")
        if not isinstance(waiver["advisory"], str) or not waiver["advisory"].startswith("https://"):
            raise ValueError("audit exception requires an exact HTTPS advisory")
        _advisory(waiver["advisory"])
        try:
            expiration = date.fromisoformat(waiver["expires"])
        except (ValueError, TypeError) as exc:
            raise ValueError("audit exception expiration missing/invalid") from exc
        if (expiration - today).days not in range(1, 31):
            raise ValueError("audit exception expired or longer than 30 days")
        key = _exception_id(waiver)
        if key in entries:
            raise ValueError("duplicate audit exception")
        entries[key] = waiver

    blocked, accepted, warnings, applied = [], [], [], set()
    for finding in sorted(findings, key=_sort_key):
        if not isinstance(finding, dict) or finding.get("ecosystem") not in ("npm","nuget"):
            raise ValueError("invalid audit finding")
        if _severity(finding.get("severity")) not in BLOCK:
            warnings.append(finding)
            continue
        if finding.get("advisory", "").startswith("npm:indirect:"):
            blocked.append(finding)
            continue
        key = (finding["ecosystem"], finding["package"].lower(),
               finding["version"], finding["advisory"])
        if key in entries:
            accepted.append(finding)
            applied.add(key)
        else:
            blocked.append(finding)
    if set(entries) - applied:
        raise ValueError("audit exception is stale or does not match any high/critical finding")
    return {"blocked": blocked, "accepted": accepted, "warnings": warnings}
