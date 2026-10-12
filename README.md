# Bengine

A provider-independent **database schema AST** framework in C# — not an ORM. A
schema is modelled as a strictly-typed, immutable tree and rendered to SQL DDL
(PostgreSQL, SQL Server, MySQL, SQLite) and language models (C#, Go, Rust,
TypeScript, Python, PHP) through a metadata-driven mapping and generation layer.

## Layout

```
bengine/
├── src/
│   ├── Bengine/              Framework library (the AST, metadata, mapping,
│   │                         validation, generators, visitors, serialization)
│   ├── Bengine.Introspection/ Live-database connectivity: connect, discover,
│   │                         introspect into the AST, and browse table data
│   ├── Bengine.Cli/          Console demo: schema.json → DDL + models
│   └── Bengine.Api/          ASP.NET Core API exposing generation + the explorer
├── designer/                 React + React Flow designer and Database Explorer
├── examples/                 Sample schema.json documents
└── Bengine.slnx
```

The single contract between the pieces is **schema.json**: the designer produces
it, `Bengine.SchemaSerializer` deserializes it into the AST, and the generators
consume the AST.

The AST can also be built **from existing SQL**: `Bengine.Parsing` reverses the
generators, parsing `CREATE TABLE` / `CREATE INDEX` / `ALTER TABLE` DDL into the
AST and auto-detecting the source dialect (PostgreSQL, SQL Server, MySQL,
SQLite). The API exposes this at `POST /api/import/sql`, and the designer's
**Import SQL** button feeds pasted DDL straight onto the canvas.

## Database Explorer (live databases)

The AST can also be built **from a running database**. `Bengine.Introspection`
connects to a live PostgreSQL, SQL Server, MySQL, or SQLite database and offers
three capabilities over one `DatabaseSession`, the way a database tool does:

- **Discover** — list databases, schemas, and relations (`IDatabaseExplorer`).
- **Introspect** — read a relation, or a whole schema, into the same
  provider-independent `DatabaseSchema` AST the generators consume, so a live
  database round-trips straight back out as DDL, code, or a migration
  (`IDatabaseIntrospector`). This is the live-connection counterpart of
  `Bengine.Parsing`.
- **Browse** — read table data a page at a time with filtering and sorting
  (`ITableDataReader`). Identifiers are validated against the relation's real
  columns and quoted; every value is bound as a parameter, so queries cannot be
  injected.

```csharp
await using var session = await DatabaseSession.OpenAsync(
    DatabaseProvider.Sqlite, "Data Source=app.db");

foreach (var table in await session.Explorer.GetTablesAsync())
    Console.WriteLine(table.QualifiedName);

var page = await session.Data.QueryAsync(
    new TableRef("users"),
    new DataQuery
    {
        Page = 1,
        PageSize = 100,
        Filters = [FilterCondition.Contains("email", "@example.com")],
        Sort = [new SortSpec("created_at", SortDirection.Descending)],
        IncludeTotalCount = true,
    });
```

On top of browsing, the engine also **edits** data (`ITableDataWriter`:
insert/update/delete, keyed by primary key and fully parameterized), **runs
ad-hoc SQL** (`IQueryExecutor`, the query console), **renders DDL** from the live
catalog (`SchemaDdlRenderer`, reusing the generators), and **exports** rows as
CSV/JSON/SQL (`DataExporter`).

### Object explorer (pgAdmin-style)

`Bengine.Introspection.Tree` adds a lazily loaded object tree over **registered
servers**, following pgAdmin's hierarchy:

```
Servers (n)
└─ server
   ├─ Databases (n) ─ database
   │  ├─ Casts · Catalogs · Event Triggers · Extensions · Foreign Data Wrappers
   │  ├─ Languages · Publications · Subscriptions
   │  └─ Schemas (n) ─ schema
   │     ├─ Aggregates · Collations · Domains · Foreign Tables · Functions
   │     ├─ Materialized Views · Procedures · Sequences · Trigger Functions · Types · Views
   │     └─ Tables (n) ─ table ─ Columns · Constraints · Indexes · Triggers
   ├─ Login/Group Roles (n)
   └─ Tablespaces (n)
```

Every folder carries its child count, and every object has **Properties** and
**SQL** (PostgreSQL's own `pg_get_*def` output where it exists; tables are
rendered by Bengine's generator from the introspected AST). MySQL, SQL Server,
and SQLite use the subset of the hierarchy that applies to them.

Servers are saved in `~/.config/bengine/servers.json` (owner-only permissions;
`BENGINE_SERVERS_FILE` overrides the path), shared by the web and desktop
explorers, and can be **imported from a local pgAdmin 4**. pgAdmin encrypts its
saved passwords, so imported servers prompt once on first connect ("Save
password" stores it in the file; otherwise it is kept in memory for the
session).

Two front ends drive the engine, both laid out like pgAdmin — the Object
Explorer on the left; Properties and SQL for the selected node; closable
**View/Edit Data** tabs (paging, per-column filters, click-to-sort, inline
edit, add/delete rows, CSV/JSON/SQL export) and **Query Tool** tabs (run the
selection or everything with F5 / Ctrl+Enter, EXPLAIN, result types under the
column names):

- **Web** — the `designer`'s **Database Explorer** tab, over the API:
  `/api/servers` (list, add/edit, remove, test, connect, disconnect, pgAdmin
  import), `/api/tree/children` and `/api/tree/properties`, and the per-database
  `/api/db/*` endpoints (`/data`, `/table`, `/query`, `/row/insert`,
  `/row/update`, `/row/delete`, `/export`, …), which take either a registered
  `serverId` + `database` or a raw `provider` + `connectionString`.
- **Desktop** — `Bengine.Studio`, an Avalonia app over the same engine
  in-process.

PostgreSQL results are read in text format, as pgAdmin does: every type
(including ones without a binary form, such as `aclitem` or extension types)
can be browsed, and numbers, timestamps, arrays, and JSON appear exactly as the
server prints them. Values typed into filters and edits are sent untyped, so
PostgreSQL coerces them to the column's type (`uuid`, `timestamp`, `bigint`, …).

> The explorer acts with the credentials you give it: the Data tabs and Query
> Tool **modify live data** when you edit rows or run DML/DDL. The API listens
> on localhost only and holds session passwords in memory — do not expose it to
> other machines, and connect only to databases you are authorized to use.

## Run

Build everything:

```bash
dotnet build Bengine.slnx
```
endi
Generate from a schema.json — or import a raw `.sql` DDL script (the dialect is
auto-detected) — with the CLI:

```bash
dotnet run --project src/Bengine.Cli -- examples/shop.schema.json
dotnet run --project src/Bengine.Cli -- examples/postgres.sql
```

Start the generation API (used by the designer):

```bash
dotnet run --project src/Bengine.Api        # http://localhost:5099
```

Run the designer and Database Explorer (proxies `/api` to the backend):

```bash
cd designer
npm install
npm run dev                                  # http://localhost:5173
```

Run the desktop Database Explorer (Avalonia):

```bash
dotnet run --project src/Bengine.Studio
```

Live-database tests run against PostgreSQL when `BENGINE_TEST_PG` is set to
`host;port;database;user;password`; the desktop UI tests render screenshots to
the directory named by `BENGINE_SHOTS`.

## Design rules

See [src/Bengine/SPEC.md](src/Bengine/SPEC.md) for the full framework
requirements. In short: strict typing (enums over strings, no `dynamic`),
immutability (`record` value objects, `FrozenDictionary` maps), metadata as the
single source of truth, and a clean ownership model — a column knows nothing of
indexes or keys; the table owns its constraints; the database owns its objects.
