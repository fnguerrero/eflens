# EfLens

[![CI](https://github.com/fnguerrero/eflens/actions/workflows/ci.yml/badge.svg)](https://github.com/fnguerrero/eflens/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A read-only [Model Context Protocol](https://modelcontextprotocol.io) server, written in C# / .NET 10, that lets AI agents
(Claude Code, VS Code, any MCP client) inspect a relational database **safely**, and that understands your
**Entity Framework Core** model.

Most MCP servers are written in Python or TypeScript, and the .NET ones focus on navigating source code with Roslyn.
EfLens covers the other half of a .NET app: the database, and whether it still matches the EF Core model.

> **Status:** early development. Schema tools work on SQLite and SQL Server. See the [roadmap](#roadmap).

## Tools

| Tool | What it does |
|---|---|
| `list_tables` | Tables and views of the connected database, with their schema, without system tables |
| `describe_table` | Columns (declared type, nullability, default, primary key position) and indexes of a table or view |
| `list_relations` | Foreign keys with their columns and `ON DELETE` action; pass a table to get the ones in and out of it |

Every tool is annotated `readOnlyHint: true`, `destructiveHint: false`. Names can be plain (`Orders`),
schema-qualified (`sales.Orders`) or bracketed (`[sales].[Orders]`); an ambiguous name returns an error that lists
the candidates, so the agent can retry on its own.

| Provider | `--provider` | Example connection |
|---|---|---|
| SQLite | `sqlite` | `Data Source=app.db` |
| SQL Server | `sqlserver` (or `mssql`) | `Server=localhost;Database=Shop;User Id=eflens_reader;Password=...;TrustServerCertificate=True` |

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/fnguerrero/eflens.git
cd eflens
dotnet build
```

### Claude Code

```bash
claude mcp add eflens -- dotnet run --project src/EfLens -- --provider sqlite --connection "Data Source=/path/to/app.db"
```

### VS Code (`.vscode/mcp.json`)

```json
{
  "servers": {
    "eflens": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "src/EfLens", "--", "--provider", "sqlite", "--connection", "Data Source=app.db"]
    }
  }
}
```

Configuration can also come from environment variables: `EfLens__Provider`, `EfLens__ConnectionString`.

## Security model

EfLens is built to be pointed at real databases, so it never trusts the caller to stay read-only:

- **Forced read-only connections.** SQLite connections are always rewritten to `Mode=ReadOnly`, even if the
  connection string asks for write mode. A missing database file is an error, never a new empty database.
- **SQL Server reads catalog views only.** Names are bound as parameters and resolved with `QUOTENAME`/`OBJECT_ID`,
  never concatenated into SQL. Connections declare `ApplicationIntent=ReadOnly`; the real guarantee is the login,
  so give EfLens a user that only has `db_datareader`.
- **Errors meant for the agent, nothing else.** A missing or ambiguous table comes back as a tool error that says how
  to recover; driver exceptions keep the SDK's generic message and do not leak connection details.
- **stdout is reserved for the protocol.** All logs go to stderr.
- **Dependencies are audited.** The build fails on known vulnerable packages (`-warnaserror` includes NuGet audit).
- Coming next: SQL validation with a real parser (SELECT only), read-only transactions, row and time limits.

## Tests

```bash
dotnet test
```

xUnit v3 on Microsoft.Testing.Platform.

- Provider tests against throwaway SQLite files.
- SQL Server tests against a real server started with [Testcontainers](https://dotnet.testcontainers.org/).
  Without Docker they are skipped locally; in CI (Ubuntu) Docker is required, so they can never be silently skipped.
- End-to-end tests that start the real server and talk to it through the official MCP client over in-memory pipes.
- CI runs on Ubuntu and Windows, with warnings as errors, TRX reports and code coverage.

## Roadmap

- [x] stdio server on the official MCP C# SDK, `list_tables` on SQLite, CI
- [x] `describe_table` and `list_relations` (columns, keys, indexes); SQL Server via Testcontainers
- [ ] Safe `query`: parser-validated SELECT, read-only transaction, row and time limits
- [ ] Load an EF Core `DbContext` and map entities to tables
- [ ] Drift detection: pending migrations and model vs. schema differences
- [ ] PostgreSQL and execution plans (`explain`)
- [ ] Ship as a .NET global tool on NuGet and list it in the MCP Registry

## License

[MIT](LICENSE)
