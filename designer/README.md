# Bengine Designer

Visual database schema designer. Tables are nodes, foreign keys are edges. The
only output is `schema.json` — the exact contract the Bengine C# framework
consumes to generate SQL (PostgreSQL, SQL Server, MySQL, SQLite) and language
models (C#, Go, Rust, TypeScript, Python, PHP).

## Run

```bash
npm install
npm run dev      # http://localhost:5173
npm run build    # type-check + production bundle
```

For live SQL/code generation, also start the Bengine backend API (it uses the
Bengine C# library directly). Vite proxies `/api` to it:

```bash
dotnet run --project ../src/Bengine.Api    # http://localhost:5099
```

## Usage

- **+ Jadval** — add a table (starts with an identity `id` primary key).
- Drag a column's right handle to another column's left handle to create a
  foreign key.
- Right dock has three tabs:
  - **Jadval** — full column editing (type, length/precision/scale, fractional
    seconds, null/PK/identity/unsigned, charset/collation, default, generated
    expression, validation, comment), plus indexes, check constraints, and
    foreign-key `onDelete`/`onUpdate`.
  - **Obyektlar** — schema-level views, sequences, and triggers.
  - **JSON** — live, read-only `schema.json` preview with copy.
  - **Kod/SQL** — pick a provider or language and generate real DDL / models
    live from the Bengine backend (requires the API running).
- **↶ / ↷** — undo / redo (structural edits). The design autosaves to
  `localStorage` and restores on reload.
- **Export schema.json** — download the document.
- **Import** — load an existing `schema.json` back into the canvas.

Everything the Bengine C# AST supports is editable here; the exported document
covers the full model.

## Contract

`src/schema/contract.ts` mirrors the C# `DatabaseSchema` AST one-to-one: enum
values are the exact C# member names and property names are camelCase, so the
exported document deserializes directly via `SchemaSerializer.FromJson`.

## Pipeline

```
Designer (React) ──schema.json──▶ Bengine.SchemaSerializer ──▶ DatabaseSchema AST
                                                                     │
                              ┌──────────────────────────────────────┤
                              ▼                                       ▼
                     SQL generators (4 providers)         Code generators (6 languages)
```

Feed an exported file straight into the backend:

```bash
dotnet run --project ../src/Bengine.Cli -- path/to/schema.json
```
