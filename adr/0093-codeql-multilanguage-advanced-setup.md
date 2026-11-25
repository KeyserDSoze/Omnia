# ADR 0093 — Reviewed CodeQL advanced setup for multi-language source scanning

- Date: 2026-11-23
- Status: implementation proposed; completion requires real CodeQL/CI/OIDC/Pages evidence on same SHA
- Child: #211; epic #10 and parent supply-chain #22 remain OPEN

## Context

Tao's locked restores and official npm/NuGet advisory gates detect vulnerable dependencies, but not first-party source-code data-flow vulnerabilities. Tao's actual source includes .NET 10/C#, Expo TypeScript, Python scripts and GitHub Actions YAML. A source scanner complements but cannot replace SCA, runtime authentication and data protection tests.

## Decision

- Add `.github/workflows/codeql.yml` as an independent CodeQL v4 **advanced setup** workflow for `actions`, `csharp`, `javascript-typescript`, `python`, running with `security-extended` on main pushes, PRs, weekly schedule and explicit manual dispatch.
- Restrict all default permissions to read-only contents; only the scanner job additionally gets `security-events: write` and `actions: read`. No deployment/auth credentials or action-invoked shell commands. Keep scan results visible; never swallow scan failures or bypass severity gates.
- Use CodeQL-supported **build-mode none**, including C#, to avoid duplicate NuGet restore and external services in SAST; acknowledge that such analysis is not equivalent to a compiled runtime trace and may have coverage limitations. CI owns genuine locked builds and E2E.
- Pin a small *reviewed workflow recipe* in a Python standard-library validator invoked from existing CI. It rejects **any** drift without the corresponding deliberate test+recipe update; this protects against disabling CodeQL or silently expanding token permissions, but is not a YAML semantic validator or a scanner. Actual GitHub Actions matrix and SARIF publishing are authoritative.
- Keep #22/#15/#10/#195 and live audit #196 open; no claims about production, GDPR, SLSA, native parity or absence of vulnerabilities.

## Evidence boundary and TDD

RED: 8-test suite initially failed importing the not-yet-written validator, then, after validator creation, failed the real-file check because `codeql.yml` did not exist. GREEN: all eight tests and the validator passed locally after adding the exact workflow; local PyYAML parsed the four languages. Only actual GitHub Actions and CodeQL matrix jobs can confirm external scanning, and their results must be noted in #211 before closure.

## Operations

See [CodeQL runbook](../docs/codeql-scanning.md) for default-vs-advanced setup, triage, permissions and coverage. Official documentation: https://docs.github.com/en/code-security/reference/code-scanning/workflow-configuration-options ; https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/configure-code-scanning/configuring-advanced-setup-for-code-scanning
