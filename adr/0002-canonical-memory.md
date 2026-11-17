# ADR 0002 — Canonical personal events, provenance and vectors
Date: 2026-11-17. Status: accepted for initial schema.

## Decision
Keep raw content, normalized JSONB, source/external ID, timestamp, tags and summary separately. Dedupe by tenant+subject+source+external ID. Store vector(768) nullable with recorded model; never synthesize embeddings. Initial deterministic tagging is a stopgap, not AI profiling. Schema migration is versioned SQL under a transaction-level advisory lock. Later: binary object storage with hashes, thread graph, outbox retry, legal deletion/retention.

## Risks
Raw data can contain other people's sensitive information. Delay use of real data until consent, encryption, retention, export/delete and tenant hardening are implemented and reviewed.
