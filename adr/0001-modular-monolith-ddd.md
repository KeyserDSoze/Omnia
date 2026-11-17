# ADR 0001 — .NET 10 modular monolith, DDD and TDD
Date: 2026-11-17. Status: accepted for foundation.

## Context
Multiple integration and inference domains need strict boundaries, but premature microservices add deployment and consistency cost.

## Decision
.NET 10 with Domain (rules), Application (ports/use cases), Infrastructure (adapters), API (auth/HTTP/DI); PostgreSQL 17+ pgvector; Expo 57 React Native for web/native. Functional boundaries evolve to modular bounded contexts before extraction. GitHub Pages hosts static assets only. Every feature adds xUnit/Vitest/Playwright coverage appropriate to the seam; verify actual runs before claiming passes.

## Consequences
Keep Azure SDKs and Npgsql outside Domain/Application. Initial system is deliberately thin; no fake Azure deployment, login or cross-channel synchronization.
