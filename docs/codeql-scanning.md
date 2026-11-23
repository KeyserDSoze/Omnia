# CodeQL advanced setup — Omnia SAST (#211)

The dedicated `.github/workflows/codeql.yml` performs static source analysis with GitHub's official CodeQL v4 actions and `security-extended` queries. It has a **four-language matrix**: `actions` for GitHub Actions YAML, `csharp` for all backend/test .NET sources, `javascript-typescript` for Expo and Web, `python` for quality/recovery scripts. No personal data, database connection, deployment key or synthetic tenant secrets are needed.

## Triggers and permissions

CodeQL runs on every push to `main`, pull requests targeting `main`, a Tuesday weekly schedule (04:17 UTC), and manual `workflow_dispatch`. The top-level token is `contents: read`; **only** the scanning job receives `security-events: write` (SARIF publishing), `actions: read` and `contents: read`. No `pull_request_target`, untrusted `run` step, secrets, auto-merge or `continue-on-error` are allowed. When PRs come from forks, GitHub applies its own token/event restrictions; review the actual run rather than assuming a successful upload.

The analyzer uses **build-mode none** including C#, which GitHub supports but which can provide less accurate cross-project/dependency context than compiled tracing. Existing locked NuGet build/restore, SCA, Playwright and real OIDC/PostgreSQL/Keycloak jobs are **not** replaced by CodeQL.

## Fail-closed source recipe test

The CI `dotnet` job runs eight Python stdlib regressions and `verify_codeql_policy.py` that compare the **complete small workflow** to a reviewed exact recipe. A harmless whitespace change also requires explicit review of the recipe; this is intentional. Negative fixtures exercise missing languages, missing triggers, token escalation, scanner/query downgrade, suppressed errors, arbitrary shell and deceptive duplicate settings. This is **not a general-purpose YAML interpreter or security scanner**. GitHub Actions execution and CodeQL uploaded alerts are the authoritative external signals. The actual YAML was independently parsed in local development, not on the production service.

## Operator verification and triage

1. Open [CodeQL runs](https://github.com/KeyserDSoze/Omnia/actions/workflows/codeql.yml) for the final `main` SHA and confirm all four matrix jobs completed successfully; confirm CI/OIDC/Pages on the same SHA.
2. Check repository **Security → Code scanning** and verify CodeQL is in **advanced setup**; if default setup was enabled, GitHub may disable the manual workflow or reject advanced-results uploads. Switch to advanced setup in repository Settings as needed. Never describe a workflow file alone as working SAST.
3. Triage any result with the relevant rule, source path, actual data-flow/reachability, severity, reviewer, issue and mitigation. Don't suppress alerts or change query suites to make a red gate green. CodeQL's successful *execution* doesn't mean zero findings.
4. Investigate unexpected missing-language, upload-permission, CodeQL version or scan-coverage failures. Do not turn a failure into warning or `continue-on-error`.
5. Retain independent npm/NuGet live SCA, lock-based SBOM, Docker index pin, full browser E2E and native EAS/device tests. CodeQL does **not** attest CVE-free packages, credential scanning, license clearance, image layers, containers, provider behavior, GDPR compliance or shipping readiness.

References: [GitHub advanced setup](https://docs.github.com/en/code-security/how-tos/find-and-fix-code-vulnerabilities/configure-code-scanning/configuring-advanced-setup-for-code-scanning), [CodeQL workflow options](https://docs.github.com/en/code-security/reference/code-scanning/workflow-configuration-options), [CodeQL compiled language modes](https://docs.github.com/en/code-security/concepts/code-scanning/codeql/codeql-for-compiled-languages).
