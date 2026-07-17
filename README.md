# tracer-net

A Linear-clone issue tracker backend built with C# / .NET 10 and ASP.NET Core.

## Stack

- .NET 10, ASP.NET Core Web API (controllers)
- EF Core with SQLite
- xUnit (unit + integration tests via `WebApplicationFactory`)

## Solution layout

| Project | Purpose |
|---|---|
| `Tracer.Api` | ASP.NET Core Web API host, controllers, DTOs |
| `Tracer.Domain` | Domain entities and core business rules |
| `Tracer.Infrastructure` | EF Core `DbContext`, migrations, persistence |
| `Tracer.Tests` | Unit and integration tests |

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/Tracer.Api
```

## Domain scope

Teams, projects, issues (priority/estimate), per-team workflow states,
labels, comments, cycles (time-boxed iterations), issue ordering,
search/filtering, and validated state transitions.
