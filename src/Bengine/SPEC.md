# Goal

Build a production-ready, provider-independent database schema framework in C#.

This is **NOT** an ORM.
This is **NOT** Entity Framework.
This is a **Database Schema AST (Abstract Syntax Tree)** that represents a database independently from any DBMS.

The framework must be designed for long-term extensibility, strict typing, compile-time safety, and code generation.

---

# Requirements

Use .NET 10.

Use nullable reference types.

Use file-scoped namespaces.

Use `required` where appropriate.

Use `FrozenDictionary` for immutable mappings.

Use `record` only for immutable value objects.
Use `sealed class` for entities.

Never use dynamic.

Avoid object unless absolutely necessary.

Avoid string when an enum can represent the value.

Avoid bool when an enum represents more than two states.

Follow SOLID.

Follow SRP.

Everything must be provider-independent.

---

# Project Structure

```
Jwc.Schema
│
├── Abstractions
├── Constants
├── Enums
├── Extensions
├── Metadata
├── Models
├── Validation
├── Mapping
├── Generators
├── Visitors
```

Generate every folder and every file.

---

# Models

Generate complete models for:

* DatabaseSchema
* Table
* Column
* Index
* IndexColumn
* PrimaryKey
* ForeignKey
* CheckConstraint
* Sequence
* View
* Trigger

Also generate:

Models/Options

including

* ColumnTypeOptions
* DefaultValue
* IdentityOptions
* GeneratedColumn
* ProviderOptions

Validation models

* ColumnValidation
* NumericValidation
* StringValidation
* DateValidation

Everything must contain XML documentation.

---

# Enums

Generate enums for everything possible.

Examples:

DatabaseProvider

DbDataType

Language

Nullability

CascadeAction

IdentityStrategy

GeneratedKind

DefaultValueKind

TypeCapability

ValidationKind

IndexType

SortDirection

Charset

Collation

StorageStrategy

IndexMethod

ReferentialAction

Any other enums that improve strict typing.

Never use strings when an enum is possible.

---

# Metadata

Metadata is the single source of truth.

Generate:

DbDataTypeMetadata

DbDataTypeRegistry

DbTypeLimits

Each DbDataTypeMetadata must contain:

* supported providers
* supported languages
* supported validations
* supported capabilities
* default length
* minimum length
* maximum length
* minimum value
* maximum value
* default precision
* maximum precision
* default scale
* maximum scale

Everything must be immutable.

---

# Mapping

Generate mappings for

PostgreSQL

SQL Server

MySQL

SQLite

C#

Go

Rust

TypeScript

Python

PHP

Provide

DbType → Provider

Provider → DbType

DbType → Language

Language → DbType

using FrozenDictionary.

---

# Validation

Generate validators.

DatabaseValidator

TableValidator

ColumnValidator

Validation rules

Provider rules

Capability rules

Length rules

Precision rules

Scale rules

Validation rules

Every validator must use metadata instead of hardcoded logic.

---

# Generators

Generate SQL generators for

PostgreSQL

SQL Server

MySQL

SQLite

Generate code generators for

C#

Go

Rust

TypeScript

Python

PHP

All generators must use metadata.

Never hardcode database types.

---

# Visitor Pattern

Generate visitor interfaces and implementations.

SchemaVisitor

SqlVisitor

ValidationVisitor

---

# Design Rules

Column must never know about indexes.

Column must never know about primary keys.

Column must never know about foreign keys.

Table owns:

Indexes

ForeignKeys

PrimaryKey

CheckConstraints

Database owns:

Tables

Views

Sequences

Triggers

---

# Quality

Every public member must contain XML documentation.

Every enum member must contain XML documentation.

Use modern C# syntax.

Avoid regions.

Avoid partial unless required.

Avoid duplicate code.

Prefer composition over inheritance.

Use readonly collections.

Use immutable value objects.

Use FrozenDictionary whenever possible.

---

# Output

Generate real production-ready C# code.

Do not generate placeholders.

Do not omit files.

Do not summarize.

Generate every file completely.

If the output exceeds the context limit, continue from the previous file automatically until the entire project is complete.
