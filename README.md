# Bengine

A provider-independent **database schema AST** framework in C# — not an ORM. A
schema is modelled as a strictly-typed, immutable tree and rendered to SQL DDL
(PostgreSQL, SQL Server, MySQL, SQLite) and language models (C#, Go, Rust,
TypeScript, Python, PHP) through a metadata-driven mapping and generation layer.

## Layout

```
bengine/
├── src/
│   ├── Bengine/          Framework library (the AST, metadata, mapping,
│   │                     validation, generators, visitors, serialization)
│   ├── Bengine.Cli/      Console demo: schema.json → DDL + models
│   └── Bengine.Api/      ASP.NET Core API exposing generation over HTTP
├── designer/             React + React Flow visual schema designer
├── examples/             Sample schema.json documents
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

## Run

Build everything:

```bash
dotnet build Bengine.slnx
```

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

Run the designer (proxies `/api` to the backend):

```bash
cd designer
npm install
npm run dev                                  # http://localhost:5173
```

## Design rules

See [src/Bengine/SPEC.md](src/Bengine/SPEC.md) for the full framework
requirements. In short: strict typing (enums over strings, no `dynamic`),
immutability (`record` value objects, `FrozenDictionary` maps), metadata as the
single source of truth, and a clean ownership model — a column knows nothing of
indexes or keys; the table owns its constraints; the database owns its objects.
