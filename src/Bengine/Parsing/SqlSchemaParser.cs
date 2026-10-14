namespace Bengine.Parsing;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bengine.Enums;
using Bengine.Metadata;
using Bengine.Models;
using Bengine.Models.Options;

/// <summary>
/// A hand-written recursive-descent parser that turns SQL DDL into a
/// <see cref="DatabaseSchema"/> AST. It recognizes <c>CREATE TABLE</c> (columns,
/// primary keys, foreign keys, unique/plain indexes, and check constraints),
/// standalone <c>CREATE INDEX</c>, and the constraint-adding forms of
/// <c>ALTER TABLE</c>. Statements it does not model are skipped, so a partially
/// understood script still yields the tables it can. Type spellings are resolved
/// through <see cref="SqlTypeResolver"/> and metadata, keeping the parser
/// provider-independent.
/// </summary>
public sealed class SqlSchemaParser : ISqlSchemaParser
{
    private readonly string _sql;
    private readonly IReadOnlyList<SqlToken> _tokens;
    private readonly DatabaseProvider _provider;

    // User-defined type names declared via CREATE TYPE / CREATE DOMAIN. Columns
    // of such a type resolve to Text since the AST has no logical enum type.
    private readonly HashSet<string> _customTypes = new(StringComparer.OrdinalIgnoreCase);
    private int _pos;

    /// <summary>Creates a reusable parser instance; each <see cref="Parse"/> call parses independently.</summary>
    public SqlSchemaParser()
    {
        _sql = string.Empty;
        _tokens = [];
        _provider = default;
    }

    private SqlSchemaParser(string sql, DatabaseProvider provider)
    {
        _sql = sql;
        _tokens = SqlTokenizer.Tokenize(sql);
        _provider = provider;
    }

    /// <inheritdoc />
    public DatabaseSchema Parse(string sql, DatabaseProvider provider) =>
        new SqlSchemaParser(sql, provider).ParseInternal();

    /// <summary>Parses SQL DDL as the given provider's dialect.</summary>
    /// <param name="sql">The SQL DDL text.</param>
    /// <param name="provider">The provider whose dialect the SQL is written in.</param>
    /// <returns>The reconstructed schema AST.</returns>
    /// <exception cref="SqlParseException">The SQL could not be parsed.</exception>
    public static DatabaseSchema ParseAs(string sql, DatabaseProvider provider) =>
        new SqlSchemaParser(sql, provider).ParseInternal();

    /// <summary>Detects the dialect of the SQL, then parses it.</summary>
    /// <param name="sql">The SQL DDL text.</param>
    /// <returns>The reconstructed schema together with the detected provider.</returns>
    /// <exception cref="SqlParseException">The SQL could not be parsed.</exception>
    public static SqlImportResult Import(string sql)
    {
        var provider = SqlDialectDetector.Detect(sql);
        var schema = new SqlSchemaParser(sql, provider).ParseInternal();
        return new SqlImportResult(schema, provider, Detected: true);
    }

    /// <summary>Parses the SQL as an explicitly chosen provider dialect.</summary>
    /// <param name="sql">The SQL DDL text.</param>
    /// <param name="provider">The provider whose dialect the SQL is written in.</param>
    /// <returns>The reconstructed schema together with the supplied provider.</returns>
    /// <exception cref="SqlParseException">The SQL could not be parsed.</exception>
    public static SqlImportResult Import(string sql, DatabaseProvider provider)
    {
        var schema = new SqlSchemaParser(sql, provider).ParseInternal();
        return new SqlImportResult(schema, provider, Detected: false);
    }

    private DatabaseSchema ParseInternal()
    {
        var builders = new List<TableBuilder>();

        while (!AtEnd)
        {
            if (Current.IsKeyword("CREATE"))
            {
                Advance();
                ParseCreate(builders);
            }
            else if (Current.IsKeyword("ALTER"))
            {
                Advance();
                ParseAlter(builders);
            }
            else if (Current.IsPunct('/'))
            {
                // SQL*Plus runs the buffer at a lone "/" (Oracle scripts).
                Advance();
            }
            else if (Current.IsKeyword("COMMENT"))
            {
                Advance();
                ParseComment(builders);
            }
            else
            {
                SkipStatement();
            }
        }

        if (builders.Count == 0)
        {
            throw new SqlParseException("No CREATE TABLE statements were found in the SQL.");
        }

        return new DatabaseSchema
        {
            Name = "imported",
            Tables = builders.Select(b => b.Build()).ToList(),
        };
    }

    // --- statement dispatch ---------------------------------------------------

    private void ParseCreate(List<TableBuilder> builders)
    {
        // Tolerate CREATE OR REPLACE / TEMP / GLOBAL / UNLOGGED prefixes.
        while (!AtEnd)
        {
            if (Current.IsKeyword("OR"))
            {
                Advance();
                TryKeyword("REPLACE");
                continue;
            }

            if (Current.IsKeyword("TEMP") || Current.IsKeyword("TEMPORARY")
                || Current.IsKeyword("GLOBAL") || Current.IsKeyword("LOCAL")
                || Current.IsKeyword("UNLOGGED") || Current.IsKeyword("EDITIONABLE")
                || Current.IsKeyword("NONEDITIONABLE") || Current.IsKeyword("FORCE") || Current.IsKeyword("NOFORCE"))
            {
                Advance();
                continue;
            }

            break;
        }

        if (TryKeyword("TABLE"))
        {
            ParseCreateTable(builders);
            return;
        }

        if (Current.IsKeyword("UNIQUE"))
        {
            Advance();
            if (Current.IsKeyword("INDEX"))
            {
                ParseCreateIndex(builders, IndexType.Unique);
                return;
            }

            SkipStatement();
            return;
        }

        if (Current.IsKeyword("INDEX"))
        {
            ParseCreateIndex(builders, IndexType.NonUnique);
            return;
        }

        // Oracle PL/SQL units hold semicolons in their bodies and end at a lone "/".
        if (_provider == DatabaseProvider.Oracle
            && (Current.IsKeyword("PROCEDURE") || Current.IsKeyword("FUNCTION") || Current.IsKeyword("PACKAGE")
                || Current.IsKeyword("TRIGGER") || (Current.IsKeyword("TYPE") && Peek(1).IsKeyword("BODY"))))
        {
            SkipPlSqlUnit();
            return;
        }

        // CREATE TYPE / CREATE DOMAIN declares a user-defined type (commonly a
        // PostgreSQL enum). Record its name so columns using it resolve to Text.
        if (Current.IsKeyword("TYPE") || Current.IsKeyword("DOMAIN"))
        {
            Advance();
            var (_, typeName) = ParseQualifiedName();
            _customTypes.Add(typeName);
            SkipStatement();
            return;
        }

        // CREATE VIEW / SEQUENCE / TRIGGER / etc. are not modeled by the importer.
        SkipStatement();
    }

    private void ParseCreateTable(List<TableBuilder> builders)
    {
        if (TryKeyword("IF"))
        {
            TryKeyword("NOT");
            TryKeyword("EXISTS");
        }

        var (schema, name) = ParseQualifiedName();

        // CREATE TABLE ... AS SELECT and other bodyless forms are skipped.
        if (!Current.IsPunct('('))
        {
            SkipStatement();
            return;
        }

        var builder = new TableBuilder(name, schema);
        ParseTableBody(builder);
        SkipStatement(); // trailing table options (ENGINE=, WITHOUT ROWID, ...)
        builders.Add(builder);
    }

    private void ParseTableBody(TableBuilder builder)
    {
        Expect('(');
        while (!AtEnd)
        {
            if (TryPunct(')'))
            {
                return;
            }

            if (TryPunct(','))
            {
                continue;
            }

            ParseTableElement(builder);
            SkipToBoundary();
        }

        throw new SqlParseException($"Unterminated column list for table '{builder.Name}'.");
    }

    private void ParseTableElement(TableBuilder builder)
    {
        if (TryKeyword("CONSTRAINT"))
        {
            var constraintName = Current.IsPunct('(') ? null : ReadName();
            ParseNamedConstraint(builder, constraintName);
            return;
        }

        if (Current.IsKeyword("PRIMARY"))
        {
            ParsePrimaryKey(builder, null);
            return;
        }

        if (Current.IsKeyword("FOREIGN"))
        {
            ParseForeignKey(builder, null);
            return;
        }

        if (Current.IsKeyword("UNIQUE"))
        {
            ParseUniqueConstraint(builder, null);
            return;
        }

        if (Current.IsKeyword("CHECK"))
        {
            ParseCheckConstraint(builder, null);
            return;
        }

        if (Current.IsKeyword("FULLTEXT") || Current.IsKeyword("SPATIAL"))
        {
            var type = Current.IsKeyword("FULLTEXT") ? IndexType.FullText : IndexType.Spatial;
            Advance();
            TryKeyword("KEY");
            TryKeyword("INDEX");
            ParseTableIndex(builder, type, null);
            return;
        }

        if (Current.IsKeyword("KEY") || Current.IsKeyword("INDEX"))
        {
            Advance();
            ParseTableIndex(builder, IndexType.NonUnique, null);
            return;
        }

        ParseColumn(builder);
    }

    private void ParseNamedConstraint(TableBuilder builder, string? name)
    {
        if (Current.IsKeyword("PRIMARY"))
        {
            ParsePrimaryKey(builder, name);
        }
        else if (Current.IsKeyword("FOREIGN"))
        {
            ParseForeignKey(builder, name);
        }
        else if (Current.IsKeyword("UNIQUE"))
        {
            ParseUniqueConstraint(builder, name);
        }
        else if (Current.IsKeyword("CHECK"))
        {
            ParseCheckConstraint(builder, name);
        }

        // Unknown named constraint kinds fall through; SkipToBoundary resyncs.
    }

    // --- table constraints ----------------------------------------------------

    private void ParsePrimaryKey(TableBuilder builder, string? name)
    {
        Expect("PRIMARY");
        Expect("KEY");
        TryKeyword("CLUSTERED");
        TryKeyword("NONCLUSTERED");
        builder.PrimaryKeyName = name;
        builder.TablePrimaryKey = ParseColumnNameList();
    }

    private void ParseForeignKey(TableBuilder builder, string? name)
    {
        Expect("FOREIGN");
        Expect("KEY");
        var columns = ParseColumnNameList();
        Expect("REFERENCES");
        var (refSchema, refTable) = ParseQualifiedName();
        var refColumns = Current.IsPunct('(') ? ParseColumnNameList() : new List<string>();
        var (onDelete, onUpdate) = ParseReferentialClauses();

        builder.ForeignKeys.Add(new ForeignKey
        {
            Name = name ?? $"fk_{builder.Name}_{refTable}",
            Columns = columns,
            ReferencedTable = refTable,
            ReferencedSchema = refSchema,
            ReferencedColumns = refColumns.Count > 0 ? refColumns : columns,
            OnDelete = onDelete,
            OnUpdate = onUpdate,
        });
    }

    private void ParseUniqueConstraint(TableBuilder builder, string? name)
    {
        Expect("UNIQUE");
        TryKeyword("KEY");
        TryKeyword("INDEX");
        TryKeyword("CLUSTERED");
        TryKeyword("NONCLUSTERED");

        var indexName = name;
        if (indexName is null && !Current.IsPunct('('))
        {
            indexName = ReadName();
        }

        var columns = ParseIndexColumns();
        builder.Indexes.Add(new Index
        {
            Name = indexName ?? $"ux_{builder.Name}_{builder.Indexes.Count + 1}",
            Type = IndexType.Unique,
            Columns = columns,
        });
    }

    private void ParseCheckConstraint(TableBuilder builder, string? name)
    {
        Expect("CHECK");
        var expression = CaptureParenExpression();
        builder.Checks.Add(new CheckConstraint
        {
            Name = name ?? $"ck_{builder.Name}_{builder.Checks.Count + 1}",
            Expression = expression,
        });
    }

    private void ParseTableIndex(TableBuilder builder, IndexType type, string? name)
    {
        var indexName = name;
        if (indexName is null && !Current.IsPunct('('))
        {
            indexName = ReadName();
        }

        var columns = ParseIndexColumns();
        var prefix = type == IndexType.Unique ? "ux" : "ix";
        builder.Indexes.Add(new Index
        {
            Name = indexName ?? $"{prefix}_{builder.Name}_{builder.Indexes.Count + 1}",
            Type = type,
            Columns = columns,
        });
    }

    // --- column definition ----------------------------------------------------

    private void ParseColumn(TableBuilder builder)
    {
        var name = ReadName();
        var (typeOptions, impliedAutoIncrement) = ParseType();

        // In SQL a column with neither NULL nor NOT NULL specified is nullable;
        // an explicit NOT NULL or PRIMARY KEY makes it non-nullable.
        var nullability = Nullability.Nullable;
        var defaultValue = DefaultValue.None;
        var identity = impliedAutoIncrement
            ? new IdentityOptions { Strategy = IdentityStrategy.AutoIncrement }
            : IdentityOptions.None;
        GeneratedColumn? generated = null;
        string? comment = null;
        var isPrimaryKey = false;
        var isUnique = false;

        while (!AtEnd && !Current.IsPunct(',') && !Current.IsPunct(')'))
        {
            if (Current.IsKeyword("NOT"))
            {
                Advance();
                if (TryKeyword("NULL"))
                {
                    nullability = Nullability.NotNull;
                }

                continue;
            }

            if (Current.IsKeyword("NULL"))
            {
                Advance();
                nullability = Nullability.Nullable;
                continue;
            }

            if (Current.IsKeyword("PRIMARY"))
            {
                Advance();
                TryKeyword("KEY");
                isPrimaryKey = true;
                nullability = Nullability.NotNull;
                continue;
            }

            if (Current.IsKeyword("UNIQUE"))
            {
                Advance();
                TryKeyword("KEY");
                isUnique = true;
                continue;
            }

            if (Current.IsKeyword("DEFAULT"))
            {
                Advance();
                if (Current.IsKeyword("ON") && Peek(1).IsKeyword("NULL"))
                {
                    // Oracle: DEFAULT ON NULL expr.
                    Advance();
                    Advance();
                }

                defaultValue = ParseDefaultOperand();
                continue;
            }

            if (Current.IsKeyword("AS") && Peek(1).IsPunct('('))
            {
                // Oracle virtual column: name type AS (expr) [VIRTUAL].
                Advance();
                generated = new GeneratedColumn { Kind = GeneratedKind.Virtual, Expression = CaptureParenExpression() };
                TryKeyword("VIRTUAL");
                continue;
            }

            if (Current.IsKeyword("AUTO_INCREMENT") || Current.IsKeyword("AUTOINCREMENT"))
            {
                Advance();
                identity = new IdentityOptions { Strategy = IdentityStrategy.AutoIncrement };
                continue;
            }

            if (Current.IsKeyword("IDENTITY"))
            {
                Advance();
                if (Current.IsPunct('('))
                {
                    SkipBalanced();
                }

                identity = new IdentityOptions { Strategy = IdentityStrategy.Identity };
                continue;
            }

            if (Current.IsKeyword("GENERATED"))
            {
                Advance();
                ParseGenerated(ref identity, ref generated);
                continue;
            }

            if (Current.IsKeyword("REFERENCES"))
            {
                Advance();
                ParseInlineReferences(builder, name);
                continue;
            }

            if (Current.IsKeyword("CHECK"))
            {
                Advance();
                var expression = CaptureParenExpression();
                builder.Checks.Add(new CheckConstraint
                {
                    Name = $"ck_{builder.Name}_{name}",
                    Expression = expression,
                });
                continue;
            }

            if (Current.IsKeyword("COLLATE"))
            {
                Advance();
                if (!Current.IsPunct(',') && !Current.IsPunct(')'))
                {
                    ReadName();
                }

                continue;
            }

            if (Current.IsKeyword("CHARACTER"))
            {
                Advance();
                TryKeyword("SET");
                if (!Current.IsPunct(',') && !Current.IsPunct(')'))
                {
                    ReadName();
                }

                continue;
            }

            if (Current.IsKeyword("CHARSET"))
            {
                Advance();
                TryPunct('=');
                if (!Current.IsPunct(',') && !Current.IsPunct(')'))
                {
                    ReadName();
                }

                continue;
            }

            if (Current.IsKeyword("COMMENT"))
            {
                Advance();
                TryPunct('=');
                if (Current.Kind == SqlTokenKind.String)
                {
                    comment = Unquote(Current.Text);
                    Advance();
                }

                continue;
            }

            if (Current.IsKeyword("ON"))
            {
                // MySQL: ON UPDATE CURRENT_TIMESTAMP — consume and ignore.
                Advance();
                TryKeyword("UPDATE");
                TryKeyword("DELETE");
                ParseDefaultOperand();
                continue;
            }

            if (Current.IsKeyword("UNSIGNED"))
            {
                Advance();
                typeOptions = typeOptions with { IsUnsigned = true };
                continue;
            }

            if (Current.IsKeyword("ZEROFILL") || Current.IsKeyword("SIGNED"))
            {
                Advance();
                continue;
            }

            if (Current.IsPunct('('))
            {
                SkipBalanced();
                continue;
            }

            // Unknown modifier — skip a single token to guarantee progress.
            Advance();
        }

        builder.Columns.Add(new Column
        {
            Name = name,
            Type = typeOptions,
            Nullability = nullability,
            Default = defaultValue,
            Identity = identity,
            Generated = generated,
            Comment = comment,
        });

        if (isPrimaryKey)
        {
            builder.InlinePrimaryKey.Add(name);
        }

        if (isUnique)
        {
            builder.Indexes.Add(new Index
            {
                Name = $"ux_{builder.Name}_{name}",
                Type = IndexType.Unique,
                Columns = [IndexColumn.Ascending(name)],
            });
        }
    }

    private (ColumnTypeOptions Type, bool ImpliedAutoIncrement) ParseType()
    {
        if (Current.Kind != SqlTokenKind.Word)
        {
            throw new SqlParseException($"Expected a column type but found '{Describe(Current)}'.");
        }

        var name = ReadBaseTypeName();

        var args = new List<int>();
        var hasMax = false;
        if (TryPunct('('))
        {
            while (!AtEnd && !Current.IsPunct(')'))
            {
                if (Current.IsKeyword("max"))
                {
                    hasMax = true;
                    Advance();
                }
                else if (Current.IsPunct('*'))
                {
                    // Oracle NUMBER(*, s): the maximum precision.
                    args.Add(38);
                    Advance();
                }
                else if (Current.Kind == SqlTokenKind.Number)
                {
                    if (int.TryParse(Current.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        args.Add(value);
                    }

                    Advance();
                }
                else
                {
                    Advance();
                }

                // Length semantics and other qualifiers (Oracle VARCHAR2(100 BYTE)) are ignored.
                while (!AtEnd && !Current.IsPunct(',') && !Current.IsPunct(')'))
                {
                    Advance();
                }

                if (!TryPunct(','))
                {
                    break;
                }
            }

            Expect(')');
        }

        // Time-zone suffix (PostgreSQL/standard): [WITH|WITHOUT] TIME ZONE.
        var withTimeZone = false;
        if (TryKeyword("WITH"))
        {
            TryKeyword("LOCAL");
            TryKeyword("TIME");
            TryKeyword("ZONE");
            withTimeZone = true;
        }
        else if (TryKeyword("WITHOUT"))
        {
            TryKeyword("TIME");
            TryKeyword("ZONE");
        }

        // MySQL numeric modifiers may sit directly after the type.
        var unsigned = false;
        while (Current.IsKeyword("UNSIGNED") || Current.IsKeyword("SIGNED") || Current.IsKeyword("ZEROFILL"))
        {
            if (Current.IsKeyword("UNSIGNED"))
            {
                unsigned = true;
            }

            Advance();
        }

        int? firstArgument = args.Count > 0 ? args[0] : null;

        if (hasMax)
        {
            var large = name.Equals("varbinary", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("binary", StringComparison.OrdinalIgnoreCase)
                ? DbDataType.Blob
                : DbDataType.Text;
            return (new ColumnTypeOptions { DataType = large, IsUnsigned = unsigned }, false);
        }

        // A column typed with a user-defined type (enum/domain) has no logical
        // equivalent in the AST, so represent it as Text.
        if (_customTypes.Contains(name))
        {
            return (new ColumnTypeOptions { DataType = DbDataType.Text, IsUnsigned = unsigned }, false);
        }

        var resolved = _provider == DatabaseProvider.Oracle && OracleType(name, args) is { } oracle
            ? new SqlTypeResolver.Resolved(oracle, ImpliesAutoIncrement: false)
            : SqlTypeResolver.Resolve(_provider, name, firstArgument);
        var dataType = resolved.DataType;
        if (withTimeZone && dataType == DbDataType.Timestamp)
        {
            dataType = DbDataType.TimestampTz;
        }

        var metadata = DbDataTypeRegistry.Get(dataType);
        int? length = null;
        int? precision = null;
        int? scale = null;
        int? fractionalSeconds = null;

        if (metadata.Supports(TypeCapability.Precision))
        {
            if (args.Count >= 1)
            {
                precision = args[0];
            }

            if (metadata.Supports(TypeCapability.Scale) && args.Count >= 2)
            {
                scale = args[1];
            }
        }
        else if (metadata.Supports(TypeCapability.FractionalSeconds))
        {
            if (args.Count >= 1)
            {
                fractionalSeconds = args[0];
            }
        }
        else if (metadata.Supports(TypeCapability.Length))
        {
            if (args.Count >= 1)
            {
                length = args[0];
            }
        }

        var options = new ColumnTypeOptions
        {
            DataType = dataType,
            Length = length,
            Precision = precision,
            Scale = scale,
            FractionalSecondsPrecision = fractionalSeconds,
            IsUnsigned = unsigned,
        };

        return (options, resolved.ImpliesAutoIncrement);
    }

    private string ReadBaseTypeName()
    {
        var name = Current.Text;
        Advance();

        if (name.Equals("double", StringComparison.OrdinalIgnoreCase) && Current.IsKeyword("precision"))
        {
            Advance();
            return "double precision";
        }

        if (name.Equals("character", StringComparison.OrdinalIgnoreCase) && Current.IsKeyword("varying"))
        {
            Advance();
            return "character varying";
        }

        if (name.Equals("long", StringComparison.OrdinalIgnoreCase) && Current.IsKeyword("raw"))
        {
            Advance();
            return "long raw";
        }

        // Oracle INTERVAL DAY [(p)] TO SECOND / INTERVAL YEAR [(p)] TO MONTH; a trailing (p) is left as the argument.
        if (name.Equals("interval", StringComparison.OrdinalIgnoreCase) && (Current.IsKeyword("day") || Current.IsKeyword("year")))
        {
            var from = Current.Text.ToLowerInvariant();
            Advance();
            if (Current.IsPunct('('))
            {
                SkipBalanced();
            }

            TryKeyword("TO");
            var to = Current.Text.ToLowerInvariant();
            Advance();
            return $"interval {from} to {to}";
        }

        if (name.Equals("national", StringComparison.OrdinalIgnoreCase))
        {
            if (Current.IsKeyword("character") || Current.IsKeyword("char"))
            {
                Advance();
                if (Current.IsKeyword("varying"))
                {
                    Advance();
                    return "character varying";
                }

                return "character";
            }

            if (Current.IsKeyword("varchar"))
            {
                Advance();
                return "character varying";
            }
        }

        return name;
    }

    private void ParseGenerated(ref IdentityOptions identity, ref GeneratedColumn? generated)
    {
        TryKeyword("ALWAYS");
        if (TryKeyword("BY"))
        {
            TryKeyword("DEFAULT");
            if (TryKeyword("ON"))
            {
                // Oracle: GENERATED BY DEFAULT ON NULL AS IDENTITY.
                TryKeyword("NULL");
            }
        }

        TryKeyword("AS");

        if (Current.IsKeyword("IDENTITY"))
        {
            Advance();
            if (Current.IsPunct('('))
            {
                SkipBalanced();
            }

            identity = new IdentityOptions { Strategy = IdentityStrategy.Identity };
            return;
        }

        if (Current.IsPunct('('))
        {
            var expression = CaptureParenExpression();
            var kind = GeneratedKind.Stored;
            if (TryKeyword("VIRTUAL"))
            {
                kind = GeneratedKind.Virtual;
            }
            else
            {
                TryKeyword("STORED");
            }

            generated = new GeneratedColumn { Kind = kind, Expression = expression };
        }
    }

    private void ParseInlineReferences(TableBuilder builder, string columnName)
    {
        var (refSchema, refTable) = ParseQualifiedName();
        var refColumns = Current.IsPunct('(') ? ParseColumnNameList() : new List<string>();
        var (onDelete, onUpdate) = ParseReferentialClauses();

        builder.ForeignKeys.Add(new ForeignKey
        {
            Name = $"fk_{builder.Name}_{columnName}",
            Columns = new List<string> { columnName },
            ReferencedTable = refTable,
            ReferencedSchema = refSchema,
            ReferencedColumns = refColumns.Count > 0 ? refColumns : new List<string> { columnName },
            OnDelete = onDelete,
            OnUpdate = onUpdate,
        });
    }

    private (ReferentialAction OnDelete, ReferentialAction OnUpdate) ParseReferentialClauses()
    {
        var onDelete = ReferentialAction.NoAction;
        var onUpdate = ReferentialAction.NoAction;

        while (!AtEnd)
        {
            if (TryKeyword("ON"))
            {
                if (TryKeyword("DELETE"))
                {
                    onDelete = ParseReferentialAction();
                }
                else if (TryKeyword("UPDATE"))
                {
                    onUpdate = ParseReferentialAction();
                }

                continue;
            }

            if (TryKeyword("MATCH"))
            {
                if (!Current.IsPunct(',') && !Current.IsPunct(')') && Current.Kind == SqlTokenKind.Word)
                {
                    Advance(); // FULL / PARTIAL / SIMPLE
                }

                continue;
            }

            if (TryKeyword("NOT"))
            {
                TryKeyword("DEFERRABLE");
                continue;
            }

            if (TryKeyword("DEFERRABLE"))
            {
                continue;
            }

            if (TryKeyword("INITIALLY"))
            {
                if (Current.Kind == SqlTokenKind.Word)
                {
                    Advance(); // DEFERRED / IMMEDIATE
                }

                continue;
            }

            break;
        }

        return (onDelete, onUpdate);
    }

    private ReferentialAction ParseReferentialAction()
    {
        if (TryKeyword("NO"))
        {
            TryKeyword("ACTION");
            return ReferentialAction.NoAction;
        }

        if (TryKeyword("RESTRICT"))
        {
            return ReferentialAction.Restrict;
        }

        if (TryKeyword("CASCADE"))
        {
            return ReferentialAction.Cascade;
        }

        if (TryKeyword("SET"))
        {
            if (TryKeyword("NULL"))
            {
                return ReferentialAction.SetNull;
            }

            if (TryKeyword("DEFAULT"))
            {
                return ReferentialAction.SetDefault;
            }
        }

        return ReferentialAction.NoAction;
    }

    private DefaultValue ParseDefaultOperand()
    {
        var negative = false;
        if (Current.IsPunct('-'))
        {
            negative = true;
            Advance();
        }
        else if (Current.IsPunct('+'))
        {
            Advance();
        }

        if (Current.IsPunct('('))
        {
            return DefaultValue.Expression(CaptureParenExpression());
        }

        var token = Current;

        if (token.Kind == SqlTokenKind.String)
        {
            Advance();
            return DefaultValue.Literal(token.Text);
        }

        if (token.Kind == SqlTokenKind.Number)
        {
            Advance();
            var text = negative ? "-" + token.Text : token.Text;
            return DefaultValue.Literal(text);
        }

        if (token.Kind is SqlTokenKind.Word or SqlTokenKind.Quoted)
        {
            var word = token.Text;
            var start = token.Start;
            Advance();

            if (Current.IsPunct('('))
            {
                var end = SkipCallArguments();
                return IsCurrentTimestamp(word)
                    ? DefaultValue.CurrentTimestamp
                    : DefaultValue.Function(_sql[start..end]);
            }

            if (Current.IsPunct('.'))
            {
                // A qualified operand such as Oracle's seq.NEXTVAL.
                var last = token.End;
                while (Current.IsPunct('.') && Peek(1).Kind is SqlTokenKind.Word or SqlTokenKind.Quoted)
                {
                    Advance();
                    last = Current.End;
                    Advance();
                }

                return DefaultValue.Expression(_sql[start..last]);
            }

            var lower = word.ToLowerInvariant();
            return lower switch
            {
                "current_timestamp" or "now" or "getdate" or "sysdatetime"
                    or "systimestamp" or "localtimestamp" or "sysdate" => DefaultValue.CurrentTimestamp,
                "current_date" => new DefaultValue { Kind = DefaultValueKind.CurrentDate },
                "current_time" => new DefaultValue { Kind = DefaultValueKind.CurrentTime },
                "null" => new DefaultValue { Kind = DefaultValueKind.Null },
                _ => DefaultValue.Literal(word),
            };
        }

        return DefaultValue.None;
    }

    // Oracle's single NUMBER type maps by precision and scale; DATE carries a time of day.
    private static DbDataType? OracleType(string name, IReadOnlyList<int> args) => name.ToLowerInvariant() switch
    {
        "number" when args.Count == 0 || (args.Count >= 2 && args[1] != 0) => DbDataType.Decimal,
        "number" => args[0] switch
        {
            1 => DbDataType.Boolean,
            <= 3 => DbDataType.TinyInt,
            <= 5 => DbDataType.SmallInt,
            <= 10 => DbDataType.Integer,
            <= 19 => DbDataType.BigInt,
            _ => DbDataType.Decimal,
        },
        "date" => DbDataType.Timestamp,
        "float" => DbDataType.Double,
        _ => null,
    };

    // COMMENT ON TABLE t IS '...' / COMMENT ON COLUMN t.c IS '...' (PostgreSQL, Oracle).
    private void ParseComment(List<TableBuilder> builders)
    {
        if (!TryKeyword("ON"))
        {
            SkipStatement();
            return;
        }

        var onColumn = TryKeyword("COLUMN");
        if (!onColumn && !TryKeyword("TABLE"))
        {
            SkipStatement();
            return;
        }

        var parts = new List<string> { ReadName() };
        while (TryPunct('.'))
        {
            parts.Add(ReadName());
        }

        if (!TryKeyword("IS") || Current.Kind != SqlTokenKind.String)
        {
            SkipStatement();
            return;
        }

        var text = Unquote(Current.Text);
        Advance();
        var tableName = onColumn ? parts[^2] : parts[^1];
        if (builders.FirstOrDefault(b => b.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase)) is { } builder)
        {
            if (!onColumn)
            {
                builder.Comment = text.Length == 0 ? null : text;
            }
            else if (builder.Columns.FindIndex(c => c.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase)) is var index and >= 0)
            {
                builder.Columns[index] = new Column
                {
                    Name = builder.Columns[index].Name,
                    Type = builder.Columns[index].Type,
                    Nullability = builder.Columns[index].Nullability,
                    Default = builder.Columns[index].Default,
                    Identity = builder.Columns[index].Identity,
                    Generated = builder.Columns[index].Generated,
                    Comment = text.Length == 0 ? null : text,
                };
            }
        }

        SkipStatement();
    }

    // Skips a PL/SQL unit to the "/" alone on its line that ends it.
    private void SkipPlSqlUnit()
    {
        while (!AtEnd)
        {
            var token = Current;
            Advance();
            if (token.IsPunct('/') && AloneOnLine(token))
            {
                return;
            }
        }
    }

    private bool AloneOnLine(SqlToken token)
    {
        for (var i = token.Start - 1; i >= 0 && _sql[i] != '\n'; i--)
        {
            if (!char.IsWhiteSpace(_sql[i]))
            {
                return false;
            }
        }

        for (var i = token.End; i < _sql.Length && _sql[i] != '\n'; i++)
        {
            if (!char.IsWhiteSpace(_sql[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCurrentTimestamp(string word) =>
        word.ToLowerInvariant() is "current_timestamp" or "now" or "getdate"
            or "sysdatetime" or "systimestamp" or "localtimestamp";

    // --- ALTER TABLE ----------------------------------------------------------

    private void ParseAlter(List<TableBuilder> builders)
    {
        if (!TryKeyword("TABLE"))
        {
            SkipStatement();
            return;
        }

        TryKeyword("ONLY");
        var (_, name) = ParseQualifiedName();
        var builder = builders.FirstOrDefault(b =>
            string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

        if (builder is null)
        {
            SkipStatement();
            return;
        }

        while (!AtEnd && !Current.IsPunct(';'))
        {
            if (TryKeyword("ADD"))
            {
                TryKeyword("COLUMN");
                var constraintName = TryKeyword("CONSTRAINT") ? ReadName() : null;

                if (Current.IsKeyword("PRIMARY"))
                {
                    ParsePrimaryKey(builder, constraintName);
                }
                else if (Current.IsKeyword("FOREIGN"))
                {
                    ParseForeignKey(builder, constraintName);
                }
                else if (Current.IsKeyword("UNIQUE"))
                {
                    ParseUniqueConstraint(builder, constraintName);
                }
                else if (Current.IsKeyword("CHECK"))
                {
                    ParseCheckConstraint(builder, constraintName);
                }
                else
                {
                    SkipToStatementBoundary();
                }
            }
            else
            {
                SkipToStatementBoundary();
            }

            if (!TryPunct(','))
            {
                break;
            }
        }

        SkipStatement();
    }

    // --- CREATE INDEX ---------------------------------------------------------

    private void ParseCreateIndex(List<TableBuilder> builders, IndexType type)
    {
        Expect("INDEX");
        if (TryKeyword("IF"))
        {
            TryKeyword("NOT");
            TryKeyword("EXISTS");
        }

        // The index name may carry its schema (Oracle, PostgreSQL): schema.index.
        var indexName = ReadName();
        while (TryPunct('.'))
        {
            indexName = ReadName();
        }

        Expect("ON");
        var (_, tableName) = ParseQualifiedName();

        // Optional USING <method>.
        if (TryKeyword("USING") && Current.Kind == SqlTokenKind.Word)
        {
            Advance();
        }

        var columns = ParseIndexColumns();

        string? filter = null;
        if (TryKeyword("WHERE"))
        {
            filter = CaptureToStatementEnd();
        }

        var builder = builders.FirstOrDefault(b =>
            string.Equals(b.Name, tableName, StringComparison.OrdinalIgnoreCase));

        builder?.Indexes.Add(new Index
        {
            Name = indexName,
            Type = type,
            Columns = columns,
            Filter = string.IsNullOrWhiteSpace(filter) ? null : filter,
        });

        SkipStatement();
    }

    // --- shared list / name helpers ------------------------------------------

    private List<string> ParseColumnNameList()
    {
        Expect('(');
        var names = new List<string>();
        while (!AtEnd)
        {
            names.Add(ReadName());
            TryKeyword("ASC");
            TryKeyword("DESC");
            if (TryPunct(','))
            {
                continue;
            }

            Expect(')');
            break;
        }

        return names;
    }

    private List<IndexColumn> ParseIndexColumns()
    {
        Expect('(');
        var columns = new List<IndexColumn>();
        while (!AtEnd)
        {
            var columnName = ReadName();

            // MySQL prefix length, e.g. name(10).
            if (Current.IsPunct('('))
            {
                SkipBalanced();
            }

            var direction = SortDirection.Ascending;
            if (TryKeyword("ASC"))
            {
                direction = SortDirection.Ascending;
            }
            else if (TryKeyword("DESC"))
            {
                direction = SortDirection.Descending;
            }

            var nulls = NullsOrder.Default;
            if (TryKeyword("NULLS"))
            {
                if (TryKeyword("FIRST"))
                {
                    nulls = NullsOrder.First;
                }
                else if (TryKeyword("LAST"))
                {
                    nulls = NullsOrder.Last;
                }
            }

            columns.Add(new IndexColumn
            {
                ColumnName = columnName,
                Direction = direction,
                Nulls = nulls,
            });

            if (TryPunct(','))
            {
                continue;
            }

            Expect(')');
            break;
        }

        return columns;
    }

    private (string? Schema, string Name) ParseQualifiedName()
    {
        var name = ReadName();
        string? schema = null;
        while (Current.IsPunct('.'))
        {
            Advance();
            schema = name;
            name = ReadName();
        }

        return (schema, name);
    }

    // --- token utilities ------------------------------------------------------

    // A non-word, non-punct sentinel so end-of-input never matches a keyword,
    // identifier, or delimiter test (default(SqlToken).Kind would be Word).
    private static readonly SqlToken EndToken = new(SqlTokenKind.Punct, string.Empty, 0, 0, '\0');

    private bool AtEnd => _pos >= _tokens.Count;

    private SqlToken Current => _pos < _tokens.Count ? _tokens[_pos] : EndToken;

    private SqlToken Peek(int offset) => _pos + offset < _tokens.Count ? _tokens[_pos + offset] : EndToken;

    private void Advance() => _pos++;

    private bool TryKeyword(string keyword)
    {
        if (Current.IsKeyword(keyword))
        {
            Advance();
            return true;
        }

        return false;
    }

    private bool TryPunct(char symbol)
    {
        if (Current.IsPunct(symbol))
        {
            Advance();
            return true;
        }

        return false;
    }

    private void Expect(char symbol)
    {
        if (!TryPunct(symbol))
        {
            throw new SqlParseException($"Expected '{symbol}' but found '{Describe(Current)}'.");
        }
    }

    private void Expect(string keyword)
    {
        if (!TryKeyword(keyword))
        {
            throw new SqlParseException($"Expected '{keyword}' but found '{Describe(Current)}'.");
        }
    }

    private string ReadName()
    {
        var token = Current;
        if (token.Kind is SqlTokenKind.Word or SqlTokenKind.Quoted)
        {
            Advance();
            return token.Text;
        }

        throw new SqlParseException($"Expected an identifier but found '{Describe(token)}'.");
    }

    private string Describe(SqlToken token) =>
        AtEnd ? "end of input" : token.Text;

    // Consumes a balanced parenthesized group starting at the current '('.
    private void SkipBalanced()
    {
        if (!Current.IsPunct('('))
        {
            return;
        }

        var depth = 0;
        while (!AtEnd)
        {
            if (Current.IsPunct('('))
            {
                depth++;
            }
            else if (Current.IsPunct(')'))
            {
                depth--;
                Advance();
                if (depth == 0)
                {
                    return;
                }

                continue;
            }

            Advance();
        }
    }

    // Consumes a call's argument list (current token is '(') and returns the
    // source offset just past the matching ')'.
    private int SkipCallArguments()
    {
        var depth = 0;
        var end = _sql.Length;
        while (!AtEnd)
        {
            var token = Current;
            if (token.IsPunct('('))
            {
                depth++;
            }
            else if (token.IsPunct(')'))
            {
                depth--;
                end = token.End;
                Advance();
                if (depth == 0)
                {
                    return end;
                }

                continue;
            }

            Advance();
        }

        return end;
    }

    // Slices the source of a parenthesized expression, excluding the outer parens.
    private string CaptureParenExpression()
    {
        if (!Current.IsPunct('('))
        {
            throw new SqlParseException($"Expected '(' but found '{Describe(Current)}'.");
        }

        var innerStart = Current.End;
        Advance();
        var depth = 1;
        while (!AtEnd)
        {
            var token = Current;
            if (token.IsPunct('('))
            {
                depth++;
            }
            else if (token.IsPunct(')'))
            {
                depth--;
                if (depth == 0)
                {
                    var expression = _sql[innerStart..token.Start].Trim();
                    Advance();
                    return expression;
                }
            }

            Advance();
        }

        throw new SqlParseException("Unterminated parenthesized expression.");
    }

    private string CaptureToStatementEnd()
    {
        if (AtEnd)
        {
            return string.Empty;
        }

        var start = Current.Start;
        var end = _sql.Length;
        var depth = 0;
        while (!AtEnd)
        {
            var token = Current;
            if (depth == 0 && token.IsPunct(';'))
            {
                end = token.Start;
                break;
            }

            if (token.IsPunct('('))
            {
                depth++;
            }
            else if (token.IsPunct(')'))
            {
                depth--;
            }

            end = token.End;
            Advance();
        }

        return _sql[start..end].Trim();
    }

    // Advances to the next top-level ',' or ')' without consuming it.
    private void SkipToBoundary()
    {
        var depth = 0;
        while (!AtEnd)
        {
            var token = Current;
            if (depth == 0 && (token.IsPunct(',') || token.IsPunct(')')))
            {
                return;
            }

            if (token.IsPunct('('))
            {
                depth++;
            }
            else if (token.IsPunct(')'))
            {
                depth--;
            }

            Advance();
        }
    }

    // Advances to the next top-level ',' or ';' without consuming it.
    private void SkipToStatementBoundary()
    {
        var depth = 0;
        while (!AtEnd)
        {
            var token = Current;
            if (depth == 0 && (token.IsPunct(',') || token.IsPunct(';')))
            {
                return;
            }

            if (token.IsPunct('('))
            {
                depth++;
            }
            else if (token.IsPunct(')'))
            {
                depth--;
            }

            Advance();
        }
    }

    private void SkipStatement()
    {
        while (!AtEnd)
        {
            var token = Current;
            Advance();
            if (token.IsPunct(';'))
            {
                return;
            }
        }
    }

    private static string Unquote(string stringLiteral)
    {
        if (stringLiteral.Length >= 2 && stringLiteral[0] == '\'' && stringLiteral[^1] == '\'')
        {
            return stringLiteral[1..^1].Replace("''", "'");
        }

        return stringLiteral;
    }

    private sealed class TableBuilder(string name, string? schema)
    {
        public string Name { get; } = name;

        public string? Schema { get; } = schema;

        public List<Column> Columns { get; } = [];

        public List<ForeignKey> ForeignKeys { get; } = [];

        public List<Index> Indexes { get; } = [];

        public List<CheckConstraint> Checks { get; } = [];

        public List<string> InlinePrimaryKey { get; } = [];

        public List<string> TablePrimaryKey { get; set; } = [];

        public string? PrimaryKeyName { get; set; }

        public string? Comment { get; set; }

        public Table Build()
        {
            var keyColumns = TablePrimaryKey.Count > 0 ? TablePrimaryKey : InlinePrimaryKey;
            PrimaryKey? primaryKey = keyColumns.Count > 0
                ? new PrimaryKey
                {
                    Name = string.IsNullOrEmpty(PrimaryKeyName) ? $"pk_{Name}" : PrimaryKeyName,
                    ColumnNames = keyColumns,
                }
                : null;

            return new Table
            {
                Name = Name,
                Schema = Schema,
                Columns = Columns,
                PrimaryKey = primaryKey,
                ForeignKeys = ForeignKeys,
                Indexes = Indexes,
                CheckConstraints = Checks,
                Comment = Comment,
            };
        }
    }
}
