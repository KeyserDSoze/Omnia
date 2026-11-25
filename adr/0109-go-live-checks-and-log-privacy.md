# ADR 0109 — Go-live: preflight, smoke test, log privacy and a managed tunnel

- Date: 2026-11-26
- Status: accepted (implemented on `main`)

## Context

Tao is about to leave the developer machine for a server behind a Cloudflare Tunnel. An audit found what
was missing: nginx logged the full request line, including OAuth codes in query strings; container logs
were unbounded; `/auth/admin` without a trailing slash was not matched by the hidden-path rules; the API
and gateway containers had no healthcheck; backups were manual; the tunnel was a loose README snippet.

## Decision

- **Log privacy.** The gateway logs `address, time, method, path, status, bytes` and never the query string
  or the referer. Container logs rotate (5 files of 10 MB). `docker/smoke.sh` proves it by sending a canary
  in the query of three requests and failing if it appears in the web, API or Keycloak logs. nginx's own
  error log may still quote a failed request: this is documented, not hidden.
- **Admin paths** `/auth/admin` and `/auth/realms/master` are hidden with and without a trailing slash.
- **`docker/preflight.sh`** checks `docker/.env` and the host before going online: https address, secrets,
  file mode, social credentials, key ring, backup folder, disk space and `compose config`. It changes
  nothing and fails on what would break or expose the installation.
- **`docker/smoke.sh <url>`** is the acceptance test of a running gateway, usable after the tunnel is up and
  in CI: routing, OIDC issuer equals the public URL, API needs a token and is `no-store`, admin hidden,
  CSP, HSTS over https, log privacy.
- **Compose profiles.** `tunnel` runs `cloudflared` with a named-tunnel token; `scheduled-backup` runs the
  backup every N hours. Neither starts by default, and the gateway still listens only on `127.0.0.1`.
- **Healthchecks** for the API (TCP request to `/health`, the image has bash but no curl) and the gateway.

## Alternatives considered

- **Redacting logs afterwards.** Rejected: a value that is never written cannot leak.
- **TLS inside the stack.** Rejected for now: the tunnel terminates TLS and nginx trusts only the
  `X-Forwarded-Proto: https` marker.

## Consequences

- An external monitor of `/api/health` and an encrypted off-host copy of backups and key ring remain the
  operator's job; the README lists them.
- Images other than the .NET ones are pinned by tag, not by digest. Updates stay manual, after a backup.
