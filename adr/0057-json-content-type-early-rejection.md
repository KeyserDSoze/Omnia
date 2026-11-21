# ADR 0057 — Reject unsupported JSON Content-Type before body reads

- Date: 2026-11-21
- Status: Accepted for narrow application middleware scope; CI evidence tracked in #158
- Issue: #158, child of #37 and epic #2; follows ADR 0055/#156 and ADR 0056/#157
- Context: ASP.NET Core 10 Minimal API request body binding and privacy resource limits

## Problem

A 1–64 KiB application byte cap and 100–30,000 ms receive budget
prevent unbounded JSON buffering and slow in-body streaming. But
unsupported request media types could still be read into memory before
Minimal API binding later rejects them. This wastes resources and
expands the pre-validation body-processing surface. No new data
store, connector or external capture is involved.

## Decision

For authenticated POST/PUT/PATCH under `/api/v1`, the existing
`ApiJsonBodyBudget` checks `Content-Encoding` (415) and advertised
oversize `Content-Length` (413), then validates the JSON media type
before requesting even one byte from the input stream.

Use ASP.NET Core `HttpRequest.HasJsonContentType()` to remain aligned
with its JSON family (`application/json` or `application/*+json`),
and `MediaTypeHeaderValue.TryParse` for parameters. The API accepts
no parameters or exactly one `charset=utf-8` (case-insensitive;
quoted UTF-8 is valid). Missing/malformed type, unsupported media,
duplicate/unknown parameters or a non-UTF-8 charset => HTTP **415**
`{"error":"unsupported_media_type"}`, `Cache-Control: no-store`,
no body reads, no binder, DB or AI work, no reflection of user content.

Content-Type enforcement applies only to JSON API mutations; do not
enable binary/multipart ingestion or apply it to GET/health. Successful
JSON proceeds to the existing bounded-read, absolute deadline and
Minimal API binder; this check is not JSON schema/content validation,
nor is it a distributed quota. OIDC+RLS and consent remain independent.

## TDD acceptance

- Test-first RED commit:
  https://github.com/KeyserDSoze/Omnia/commit/ad5f9d0f8944b225cf1589995f17c0d7434222ff
- Implementation:
  https://github.com/KeyserDSoze/Omnia/commit/ee7c598d75acec8c1591d6b0ac18f89499777f58
- `ApiJsonContentTypeTests`: fail-on-read stream rejects missing,
  `text/plain`, XML, ISO-8859-1, UTF-16, duplicate charset and
  malformed Content-Type before handler; accepted JSON and vendor +json
  preserve the original body and stream; `/health` unaffected.
- CI results, including the final ADR/docs SHA, are evidence managed
  under #158. Until observed, do not claim workflow success.
- Parent #37 remains **open**: no WAF, global quota, per-connector/media
  budget, production ingress hardening or GDPR sign-off from this slice.

## Authoritative references

- Microsoft — Minimal API parameter binding and HasJsonContentType:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding?view=aspnetcore-10.0
- Microsoft — Minimal API unsupported Content-Type and 415:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-10.0
- RFC 9110 — Content-Type and 415:
  https://www.rfc-editor.org/rfc/rfc9110.html
