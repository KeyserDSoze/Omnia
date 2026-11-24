# ADR 0097 — Installed npm notice candidates and Web export evidence

- Date: 2026-11-24
- Status: accepted for bounded implementation; external CI validation pending
- Issue #218; parents #22 and #131 remain open.

The lockfile license inventory (#216) and verified restored NuGet
inventory (#217) do not establish actual distribution obligations.
Collect root notice candidate file hashes after npm ci and the Web
export, together with the Web file manifest and exact lock digest.
Use Python standard library and no extra dependencies. Record all
locked occurrences, explicit optional absences, installed identities,
declarations and candidate hashes. Fail on missing required packages,
identity mismatch, unsafe paths, selected symlinks and malformed
export. Keep declaration differences and missing root notices visible
for review; no assumption of legal incompatibility or approval.

Run in existing mobile CI immediately after Web export, before
native exports. Upload a separate short-lived evidence artifact.
Do not mutate dist, copy notice contents into the public site,
choose SPDX OR branches or equate installed packages with distributed
modules. Separate Pages build byte equivalence is not established.

TDD: local behavioral RED against incomplete stub (8 methods:
5 failures, 3 errors); GREEN all 8 methods pass. CI must confirm
the real installed graph, export and same-SHA workflows before
closing #218. No legal decision, native build or production readiness
is implied. See docs/web-notice-evidence.md.
