namespace Bengine.Parsing;

using Bengine.Enums;
using Bengine.Models;

/// <summary>
/// The outcome of importing SQL DDL: the reconstructed schema together with the
/// provider that produced it and whether that provider was detected or supplied.
/// </summary>
/// <param name="Schema">The reconstructed schema AST.</param>
/// <param name="Provider">The provider whose dialect the SQL was parsed as.</param>
/// <param name="Detected"><c>true</c> if the provider was auto-detected; <c>false</c> if it was supplied by the caller.</param>
public sealed record SqlImportResult(DatabaseSchema Schema, DatabaseProvider Provider, bool Detected);
