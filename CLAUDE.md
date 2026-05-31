---
tags: [claude, project-template]
---

# Project: 01-mental-model

## Stack: .NET 10, Clean Architecture, xUnit+FluentAssertions

## Details: .claude/docs/dotnet-standards.md (read when generating non-trivial changes or code)

> Learning module 1 (01-mental-model.md)

## Solution layout
- `src/Domain/`              — entities, value objects, domain services. **No external deps.**
- `src/ApplicationServices/` — use cases (one folder per feature), `*Request` / `*Response` DTOs
- `src/Infrastructure/`      — EF DbContext, Dapper, HTTP clients, brokers, file I/O
- `src/Api/`                 — composition root, controllers, middleware, hosted services
- `tests/<Project>.Tests/`   — xUnit

## Key decisions
- SQLite — lightweight, no server, fits learning scope
- EF Core (writes) + Dapper (reads) — standard split per global defaults
- No CQRS, no mediator — overkill for this scope
- No external message broker

## Forbidden patterns
- No `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` — deadlock risk
- No public setters on domain entities — use methods to enforce invariants
- No lazy loading — explicit `Include` only
- No `IQueryable` leaking outside repository boundary
- No `File.*` outside `Program.cs` — use `IFileSystem`
- No string interpolation in `logger.Log*` calls — use structured placeholders
- No DB mocking in integration tests — use Testcontainers or real SQLite
- See `.claude/docs/dotnet-standards.md` for full rationale

## When generating code here
- Use Plan mode for changes touching multiple layers
- Add a unit test alongside any new domain or application service method
- Run `/review` before declaring a task done
