# EfLens

<!-- mcp-name: io.github.fnguerrero/eflens -->

[![CI](https://github.com/fnguerrero/eflens/actions/workflows/ci.yml/badge.svg)](https://github.com/fnguerrero/eflens/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A read-only [Model Context Protocol](https://modelcontextprotocol.io) server, written in C# / .NET 10, that lets AI agents
(Claude Code, VS Code, any MCP client) inspect a relational database **safely**, and that understands your
**Entity Framework Core** model.

Most MCP servers are written in Python or TypeScript, and the .NET ones focus on navigating source code with Roslyn.
EfLens covers the other half of a .NET app: the database, and whether it still matches the EF Core model.

> **Status:** early development. Schema tools, safe queries and execution plans work on SQLite, SQL Server and
> PostgreSQL; `ef_model` reads the EF Core model of a compiled app and `ef_drift` tells whether the database still
> matches it. See the [roadmap](#roadmap).

## Tools

| Tool | What it does |
|---|---|
| `list_tables` | Tables and views of the connected database, with their schema, without system tables |
| `describe_table` | Columns (declared type, nullability, default, primary key position) and indexes of a table or view |
| `list_relations` | Foreign keys with their columns and `ON DELETE` action; pass a table to get the ones in and out of it |
| `query` | Runs **one read-only SELECT** and returns `columns`, `rows` (as arrays) and `truncated`; anything else is rejected with the reason |
| `explain` | The estimated plan of that SELECT **without running it**: a tree of operations with table, index, rows and cost, plus notes (missing indexes, warnings, filtered full scans) |
| `ef_model` | Reads the EF Core model of your compiled app: entity → table → column mappings, and the tables EF Core expects (store types, nullability, defaults, keys, foreign keys, indexes) |
| `ef_drift` | Compares that model with the real database: pending migrations, applied migrations the build does not have, model changes with no migration, and every schema difference |

Every tool is annotated `readOnlyHint: true`, `destructiveHint: false`. Names can be plain (`Orders`),
schema-qualified (`sales.Orders`) or bracketed (`[sales].[Orders]`); an ambiguous name returns an error that lists
the candidates, so the agent can retry on its own.

| | SQLite | SQL Server | PostgreSQL |
|---|---|---|---|
| `--provider` | `sqlite` | `sqlserver` (or `mssql`) | `postgres` (or `postgresql`) |
| Example connection | `Data Source=app.db` | `Server=localhost;Database=Shop;User Id=eflens_reader;Password=...;TrustServerCertificate=True` | `Host=localhost;Database=shop;Username=eflens_reader;Password=...` |
| Schema tools | ✓ | ✓ | ✓ |
| `query` guard | Engine authorizer | ScriptDom parser + rolled-back transaction | Server-side single statement, plan check, `READ ONLY` transaction, cursor |
| `explain` | `EXPLAIN QUERY PLAN` | `SHOWPLAN_XML` (with missing index suggestions) | `EXPLAIN (FORMAT JSON)` |
| `ef_drift` history | `__EFMigrationsHistory` | `__EFMigrationsHistory` (any schema) | `__EFMigrationsHistory` (any schema) |
| Tested against | Throwaway files | SQL Server 2022 in a container | PostgreSQL 17 in a container |

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). There is no install step: `dnx` downloads the
[EfLens package](https://www.nuget.org/packages/EfLens) from NuGet.org and runs it. Configuration travels as
environment variables, so the connection string never shows up in a command line.

### Claude Code

```bash
claude mcp add eflens -e EfLens__Provider=sqlite -e "EfLens__ConnectionString=Data Source=/path/to/app.db" -- dnx EfLens --yes
```

On Windows, run `dnx` through the shell: `... -- cmd /c dnx EfLens --yes`.

### VS Code (`.vscode/mcp.json`)

```json
{
  "inputs": [
    { "type": "promptString", "id": "eflens-connection", "description": "Connection string (read-only login)", "password": true }
  ],
  "servers": {
    "eflens": {
      "type": "stdio",
      "command": "dnx",
      "args": ["EfLens", "--yes"],
      "env": {
        "EfLens__Provider": "sqlserver",
        "EfLens__ConnectionString": "${input:eflens-connection}",
        "EfLens__EfAssembly": "${workspaceFolder}/src/MyApp/bin/Debug/net10.0/MyApp.dll"
      }
    }
  }
}
```

Pin a version with `EfLens@0.1.0`. To keep it installed instead: `dotnet tool install -g EfLens`, then use `eflens`
as the command. To run from source: `dotnet run --project src/EfLens -- --provider sqlite --connection "Data Source=app.db"`.

| Option | Flag | Default |
|---|---|---|
| Database engine: `sqlite`, `sqlserver` or `postgres` | `--provider` | `sqlite` |
| Connection string (use a read-only login) | `--connection` | none |
| Most rows `query` returns (the agent can ask for fewer, never more) | `--max-rows` | 200 |
| Seconds before a query is cancelled in the engine | `--timeout` | 15 |
| Compiled app with the `DbContext` (its `.dll` in `bin/...`), for `ef_model` and `ef_drift` | `--ef-assembly` | none |
| `DbContext` to use when the app has more than one | `--ef-context` | none |

Every option has an environment variable too: `EfLens__Provider`, `EfLens__ConnectionString`, `EfLens__MaxRows`,
`EfLens__QueryTimeoutSeconds`, `EfLens__EfAssembly`, `EfLens__EfContext`.

## How `ef_model` reads your model

EfLens does not ship EF Core. It loads your compiled app into its own collectible `AssemblyLoadContext`, resolved
with your app's `.deps.json`, so the model is built by **your** EF Core version and provider (EF Core 10 or later).
A small probe assembly is loaded into that same context and returns the model as JSON; nothing crosses the boundary
but text.

- The `DbContext` is created the way `dotnet ef` does it at design time: an `IDesignTimeDbContextFactory<T>` first,
  then a parameterless constructor. No connection is opened.
- Assemblies are loaded from memory, so your build output is never locked: rebuild while EfLens runs and the next
  call sees the new model.
- ASP.NET Core shared-framework assemblies are resolved too, so `IdentityDbContext` works.

[`samples/Shop`](samples/Shop) is a small app with a design-time factory and two migrations, used by the tests and
handy for trying it. `dotnet Shop.dll migrate "<connection>" [migration]` builds a SQL Server database at any migration.

```bash
dotnet build samples/Shop
dotnet run --project src/EfLens -- --provider sqlserver --connection "Server=localhost;Database=Shop;..." --ef-assembly samples/Shop/bin/Debug/net10.0/Shop.dll
```

## Catching drift with `ef_drift`

The database in production and the model in your code drift apart quietly: a migration that was never deployed,
a column widened by hand during an incident, an index dropped to speed up a bulk load. `ef_drift` finds all of it
in one call, without applying anything.

A real case, reproduced by the tests against SQL Server: the Shop database was deployed up to `InitialCreate`
(the `AddOrderStatus` migration never reached it), and then someone fixed things by hand:

```sql
ALTER TABLE dbo.Customers ALTER COLUMN Name nvarchar(200) NOT NULL;  -- names were being cut
DROP INDEX IX_Customers_Email ON dbo.Customers;                     -- "it was slowing the import"
ALTER TABLE sales.OrderLines ADD Discount decimal(5,2) NULL;         -- added straight in the database
```

`ef_drift` returns:

```json
{
  "context": "Shop.ShopContext",
  "inSync": false,
  "summary": "Out of sync: 1 pending migration (20261114045603_AddOrderStatus); 4 schema differences. Pending migrations usually explain part of the differences: apply them (`dotnet ef database update`) and run ef_drift again; whatever remains was changed by hand.",
  "migrations": {
    "historyTable": "__EFMigrationsHistory",
    "historyTableExists": true,
    "applied": ["20261114045550_InitialCreate"],
    "pending": ["20261114045603_AddOrderStatus"],
    "notInAssembly": [],
    "modelChangedSinceLastMigration": false
  },
  "differences": [
    { "kind": "type_mismatch", "table": "Customers", "object": "Name", "expected": "nvarchar(100)", "actual": "nvarchar(200)" },
    { "kind": "missing_index", "table": "Customers", "object": "IX_Customers_Email", "expected": "UNIQUE (Email)", "actual": null },
    { "kind": "extra_column", "table": "sales.OrderLines", "object": "Discount", "expected": null, "actual": "decimal(5,2) NULL" },
    { "kind": "missing_column", "table": "sales.Orders", "object": "Status", "expected": "nvarchar(20) NOT NULL", "actual": null }
  ]
}
```

The agent can tell the two causes apart: `Orders.Status` comes with the pending migration, the other three were
made by hand and need either a migration or a rollback.

What is compared:

- **Migrations:** the ones compiled into the app against the history table EF Core uses (custom name and schema
  included), plus EF Core's own check for model changes that have no migration yet.
- **Tables and columns:** missing or extra, store type (spelled the same way on both sides: `datetime2` is
  `datetime2(7)`), nullability.
- **Keys:** primary key columns and order; foreign keys matched by columns and target table, with `ON DELETE`;
  indexes and unique constraints matched by columns and uniqueness. Names are not compared, so a database whose
  constraints were named by hand is not reported as different.
- Not yet: default values, computed columns, check constraints, index filters and sequences. A table without a
  schema in the model matches the database's default schema (`dbo`, or `main` in SQLite).

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

### `query` on PostgreSQL

1. **One statement, checked by the server.** Npgsql runs in raw mode, so it never splits the text on the client;
   PostgreSQL's extended protocol refuses `SELECT 1; COMMIT; DELETE ...` as a whole.
2. **Planned before it runs.** PostgreSQL must be able to `EXPLAIN` it (so `SET`, `COMMIT`, `DO`, `CALL`, `COPY`
   are refused), and a plan that writes (`ModifyTable`, including data-modifying CTEs) or locks rows is rejected.
3. **Read-only transaction, always rolled back.** The session starts with `default_transaction_read_only` and the
   query runs in `START TRANSACTION READ ONLY`: the engine refuses writes anywhere, even inside functions or
   `nextval()`.
4. **Through a cursor.** Rows are read with `DECLARE ... CURSOR` and `FETCH`, which only accept a query and make the
   server stop at the row cap instead of computing the rest; `statement_timeout` cancels it in the server.
5. **Least privilege.** Functions run with your login's rights (`dblink`, `pg_read_file`...): connect with a role
   that only has `pg_read_all_data`, or `SELECT` on what the agent may see.

### `explain`

It goes through the same guards as `query` and never executes the statement: `SHOWPLAN_XML` makes SQL Server
compile instead of run, PostgreSQL's `EXPLAIN` has no `ANALYZE`, and SQLite's `EXPLAIN QUERY PLAN` only plans.

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
- SQL Server and PostgreSQL tests against real servers started with [Testcontainers](https://dotnet.testcontainers.org/).
  Without Docker they are skipped locally; in CI (Ubuntu) Docker is required, so they can never be silently skipped.
- An attack table for PostgreSQL too: writes in CTEs, statements chained after `COMMIT`, `FOR UPDATE`, `nextval()`,
  `CREATE TABLE AS`, `DO`, `COPY`, `SET ROLE`; after each one the data is checked untouched.
- Execution plans on the three engines, including a missing index that SQL Server suggests on a 50,000-row table.
- EF Core tests that load `samples/Shop` from its build output, like any user app: store types, migration-style
  key names, migrations, context selection, wrong assemblies, and a check that the DLL is never locked.
- Drift: every rule unit tested on hand-built catalogs, and end to end on SQL Server, where Shop migrates its own
  databases (one in sync, one with a missing deploy and a hotfix by hand) and `ef_drift` must find each change.
- End-to-end tests that start the real server and talk to it through the official MCP client over in-memory pipes.
- CI runs on Ubuntu and Windows, with warnings as errors, TRX reports and code coverage, then packs the NuGet
  package, installs it as a tool and smoke tests it over stdio ([`tests/smoke`](tests/smoke)).

## Roadmap

- [x] stdio server on the official MCP C# SDK, `list_tables` on SQLite, CI
- [x] `describe_table` and `list_relations` (columns, keys, indexes); SQL Server via Testcontainers
- [x] Safe `query`: engine authorizer (SQLite), ScriptDom validation and rolled-back transaction (SQL Server), row and time limits
- [x] Load an EF Core `DbContext` (isolated load context, app's own EF Core) and map entities to tables
- [x] Drift detection: pending migrations and model vs. schema differences
- [x] PostgreSQL and execution plans (`explain`)
- [ ] Ship as a .NET global tool on NuGet and list it in the MCP Registry (packaged and automated; first release pending)

## Releasing

Tagging `vX.Y.Z` runs [`release.yml`](.github/workflows/release.yml): tests, pack, smoke test, push to NuGet.org
with [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) and publish
[`server.json`](src/EfLens/.mcp/server.json) to the [MCP Registry](https://registry.modelcontextprotocol.io) with
GitHub OIDC. No API key is stored anywhere. `EfLens.csproj` and `server.json` must carry the tag's version.

## License

[MIT](LICENSE)
