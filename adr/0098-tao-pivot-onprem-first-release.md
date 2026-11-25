# ADR 0098 — Tao: pivot to an on-prem first release

- Date: 2026-11-25
- Status: accepted
- Supersedes in part: ADR 0019–0053 (disaster-recovery research track), 0085–0093 (supply-chain
  gate implementation details). Those records stay as history.

## Context

The project ("Omnia") had grown a long research track on disaster recovery (PITR, erasure
witnesses, anchors, quarantine) and on supply-chain evidence (SBOM, licence inventories, advisory
review files, workflow validators). None of it was in production, it slowed every change, and it did
not move the business case: a personal work assistant connected to Microsoft 365 and Google that
plans the day. The owner wants a single, clean `main`, a product named **Tao**, and an installation
that runs on a personal server behind a Cloudflare Tunnel.

## Decision

1. **Scope reset.** The research track (projects, scripts, migrations 0006–0008, workflows, docs) is
   removed from `main` and preserved in the tag `archive/omnia-dr-research` (commit 1496714).
   All open PRs and branches were closed; only `main` remains.
2. **Rename** Omnia → Tao everywhere (namespaces, DB roles `tao_app`/`tao_retention`, GUCs `tao.*`,
   envelope magic `TAO1`). There is no production data, so the schema restarts from a single
   `0001_baseline.sql`; later migrations are append-only.
3. **On-prem gateway.** One port, `59800`: nginx serves the static Expo web build on `/`, proxies the
   .NET API on `/api/` and Keycloak (production mode, relative path `/auth`) on `/auth/`. The Keycloak
   admin console is only on the host port `59801`. TLS terminates at Cloudflare; nginx enables HSTS only
   when the tunnel forwards `X-Forwarded-Proto: https`. `docker/setup.sh|ps1` generate random secrets
   and the envelope key ring.
4. **Web build modes.** The same Expo app is exported for the gateway (same origin, base path `''`)
   and for GitHub Pages (`/Tao`, static demo with synthetic data only).
5. **Essential gates only.** Locked npm/NuGet restores, `npm audit`/NuGet audit failing on HIGH and
   CRITICAL, CodeQL (actions, C#, JS/TS), Dependabot monthly grouped (one PR per ecosystem, Expo-managed
   packages and majors excluded), central package versions in `Directory.Packages.props`.
   CI runs backend unit + PostgreSQL integration, web typecheck/Vitest/Playwright and a gateway job
   that boots the whole compose stack and logs in through `/auth`.

## Consequences

- Faster iteration on the business case; the privacy core stays (FORCE RLS, sealed payloads,
  export/erasure, retention, consent) and remains mandatory for new tables.
- Disaster recovery is now an operator concern (PostgreSQL backups + backup of
  `docker/secrets/envelope.keyring.json`); a future ADR may reintroduce a simpler DR story.
- The old ADRs describe mechanisms that no longer exist on `main`; read them as history.
