# ADR 0092 — Controlled Dependabot version-update proposals

- Date: 2026-11-23
- Status: accepted as a bounded implementation, pending same-SHA GitHub Actions confirmation
- Child: #197; parents #22, #15 and #10 remain open

## Context

Omnia already has deterministic npm/NuGet locks, lock-derived SBOMs, live fail-closed SCA and pinned .NET API OCI manifest indexes (ADRs 0085–0091). There was no unattended discovery workflow to *propose* upstream dependency changes. Unreviewed upgrades are risky: Expo SDK/Router and React Native ABI must be coordinated (#195), while changing pinned Docker tags without digest/architecture validation breaks the supply-chain contract.

## Decision

- Add a v2 Dependabot configuration monitoring npm under `/src/mobile`, NuGet across one-level wildcard project directories `/src/backend/*` and `/tests/*`, GitHub Actions from `/`, and Docker from `/docker`.
- Weekly version proposals only, limited to five concurrent open version PRs **per ecosystem**; keep default target `main`, existing `area:quality` label and NuGet `group-by: dependency-name` to align versions across projects.
- No ignored package versions, automatic merging, privileged registry credentials or CI bypass in this configuration. Every PR must be reviewed together with actual locked restore, CI/OIDC/Pages and security findings.
- Serialize the config as **JSON within `.yml`**, a YAML 1.2-compatible flow representation, so Python stdlib `json` parses the full exact document in CI without a new third-party scanner/parser. A fail-closed policy validator checks ecosystem coverage and the actual csproj tree. This does not execute Dependabot itself.
- A Docker update PR may fail CI until the immutable multiarch index digest and runtime patches are intentionally reconciled; that is correct, not a reason to disable the gate.

## TDD & evidence

- RED: independent seven-test Python unittest suite failed `test_actual_repository_config_is_valid` with `Dependabot YAML (JSON subset) missing or invalid` before adding the config, while the fixture checks passed. A second nested tests/project negative fixture was added in refactor.
- GREEN: local Python test suite / policy check (see commit and CI evidence in #197), plus YAML parser inspection of the JSON subset. Only successful GitHub Actions runs on the final commit may be claimed as CI evidence.

## Remaining risks

Dependabot must be enabled/operational for the repository and deliver a real PR to establish end-to-end operation. GitHub security updates and alerts have separate settings, version PR limits do not apply to security PRs, and bot proposals alone do not guarantee CVE fixes, licenses, container security, toolchain reproducibility or GDPR compliance. Keep #22/#15/#10/#195 open.

References: https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference ; https://docs.github.com/en/code-security/concepts/supply-chain-security/about-the-dependabot-yml-file
