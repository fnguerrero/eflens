# EfLens

[![CI](https://github.com/fnguerrero/eflens/actions/workflows/ci.yml/badge.svg)](https://github.com/fnguerrero/eflens/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A read-only [Model Context Protocol](https://modelcontextprotocol.io) server, written in C# / .NET 10, that lets AI agents
(Claude Code, VS Code, any MCP client) inspect a relational database **safely**, and that understands your
**Entity Framework Core** model.

Most MCP servers are written in Python or TypeScript, and the .NET ones focus on navigating source code with Roslyn.
EfLens covers the other half of a .NET app: the database, and whether it still matches the EF Core model.

> **Status:** early development. Schema tools and safe queries work on SQLite and SQL Server. See the [roadmap](#roadmap).

## Tools

| Tool | What it does |
|---|---|
| `list_tables` | Tables and views of the connected database, with their schema, without system tables |
| `describe_table` | Columns (declared type, nullability, default, primary key position) and indexes of a table or view |
| `list_relations` | Foreign keys with their columns and `ON DELETE` action; pass a table to get the ones in and out of it |
| `query` | Runs **one read-only SELECT** and returns `columns`, `rows` (as arrays) and `truncated`; anything else is rejected with the reason |

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

| Option | Flag | Default |
|---|---|---|
| Most rows `query` returns (the agent can ask for fewer, never more) | `--max-rows` | 200 |
| Seconds before a query is cancelled in the engine | `--timeout` | 15 |

Configuration can also come from environment variables: `EfLens__Provider`, `EfLens__ConnectionString`,
`EfLens__MaxRows`, `EfLens__QueryTimeoutSeconds`.

## Security model

EfLens is built to be pointed at real databases by an AI agent, so it never trusts the SQL it receives.
Every guard is enforced by the database engine or a real parser, never by keyword matching.

### `query` on SQLite

1. **Read-only file.** Connections are always rewritten to `Mode=ReadOnly`, even if the connection string asks for
   write mode. A missing file is an error, never a new empty database.
2. **Engine authorizer.** An [`sqlite3_set_authorizer`](https://www.sqlite.org/c3ref/set_authorizer.html) callback
   lets the engine itself allow only reads, functions and recursive CTEs. `INSERT`, `UPDATE`, `DELETE`, DDL,
   `PRAGMA`, `ATTACH` and transaction control are denied while the statement is being compiled.
3. **No other databases.** `SQLITE_LIMIT_ATTACHED` is set to 0, so other files on disk cannot be read.
4. **Exactly one statement.** The text is compiled by the engine and its tail must be empty or only comments.
5. **Time limit.** A runaway query (an endless recursive CTE, say) is stopped with `sqlite3_interrupt`.

### `query` on SQL Server

1. **Real T-SQL parser.** [ScriptDom](https://github.com/microsoft/SqlScriptDOM), the parser behind SQL Server's
   own tools, must find exactly one `SELECT`: no `INTO`, no linked servers, `OPENROWSET`, `OPENQUERY`,
   `OPENDATASOURCE`, `NEXT VALUE FOR` or exclusive locking hints. Comments and string literals cannot fool it.
2. **Always rolled back.** The query runs inside a transaction that is rolled back, whatever happens.
3. **Limits.** Rows stop being read at the cap and the server is told to cancel instead of draining the rest;
   the timeout cancels the command on the server.
4. **Least privilege.** The one layer EfLens cannot provide: connect with a login that only has `db_datareader`.

### Everywhere

- **Schema tools only read catalog views.** Names are bound as parameters and resolved with
  `QUOTENAME`/`OBJECT_ID` (SQL Server) or table-valued pragmas (SQLite), never concatenated into SQL.
- **Bounded results.** Long text and binary values are shortened, so one column cannot flood the agent's context.
- **Errors meant for the agent, nothing else.** Rejections, timeouts and engine errors ("Invalid column name") come
  back as tool errors that explain how to recover; any other exception keeps the SDK's generic message, so
  connection details do not leak.
- **stdout is reserved for the protocol.** All logs go to stderr.
- **Dependencies are audited.** The build fails on known vulnerable packages (`-warnaserror` includes NuGet audit).

## Tests

```bash
dotnet test
```

xUnit v3 on Microsoft.Testing.Platform.

- Provider tests against throwaway SQLite files.
- An attack table for the T-SQL validator (writes hidden in CTEs, comments and literals, `GO` batches, linked
  servers, `OPENROWSET(BULK ...)`, locking hints) and for the SQLite authorizer (`PRAGMA`, `ATTACH`, transactions).
- SQL Server tests against a real server started with [Testcontainers](https://dotnet.testcontainers.org/).
  Without Docker they are skipped locally; in CI (Ubuntu) Docker is required, so they can never be silently skipped.
- End-to-end tests that start the real server and talk to it through the official MCP client over in-memory pipes.
- CI runs on Ubuntu and Windows, with warnings as errors, TRX reports and code coverage.

## Roadmap

- [x] stdio server on the official MCP C# SDK, `list_tables` on SQLite, CI
- [x] `describe_table` and `list_relations` (columns, keys, indexes); SQL Server via Testcontainers
- [x] Safe `query`: engine authorizer (SQLite), ScriptDom validation and rolled-back transaction (SQL Server), row and time limits
- [ ] Load an EF Core `DbContext` and map entities to tables
- [ ] Drift detection: pending migrations and model vs. schema differences
- [ ] PostgreSQL and execution plans (`explain`)
- [ ] Ship as a .NET global tool on NuGet and list it in the MCP Registry

## License

[MIT](LICENSE)
