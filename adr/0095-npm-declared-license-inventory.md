# ADR 0095 — Deterministic npm declared-license inventory

- Date: 2026-11-24
- Status: accepted; same-SHA CI/OIDC/Pages/CodeQL SUCCESS on 36bba27a, #216 closed
- Child #216; parents #22, #10 and related #131 remain open.

## Context

ADR 0089 inventories locked packages and checksums but does not
evaluate license metadata. The npm v3 lock currently declares licenses
for 615 package occurrences across 14 distinct expressions. No
license compatibility or distribution clearance has been established.

## Decision

Generate a deterministic separate JSON report from the exact npm lock
bytes with SHA-256 and commit, recording each path, name, version,
unmodified declaration and dev/optional/devOptional flags. Use stdlib
Python only. Recognize exact declarations observed in the lock and
classify them as notice-review or additional-review. Neither category
is legal approval; no automatic OR choice, SPDX normalization, or
license compatibility decision. Unknown or missing metadata blocks
the inventory gate, with the report retained for review. Malformed
structure and duplicate JSON keys fail closed. The existing live
SCA and locked installs must remain independent and unchanged.

Run after the live advisory audit and artifact publication in the
read-only security-audit job (also run on preceding failure unless
canceled); upload a distinct short-lived inventory. A prior error
is never waived by inventory success. Document limits and review
workflow in docs/npm-license-inventory.md.

## Test-first evidence

Local behavioral RED: eight test methods against a deliberately
incomplete stub produced 14 assertion failures and two errors.
GREEN: the same eight methods passed on the completed implementation.
Fixtures exercise all occurrences, flags, exact declarations, unknown
and missing licenses, stable order, malformed lock data, source
digest, and duplicate keys. Real CI on the final SHA must confirm
the actual lock inventory and the full CI/OIDC/Pages/CodeQL checks;
issue #216 contains the run evidence.

## Remaining risks

No reading or verification of actual LICENSE/NOTICE texts, no
NuGet/container or installed artifact coverage, no legal approval
or release readiness. Follow up in #22 and #131. All dependencies
and lockfiles are unchanged.
