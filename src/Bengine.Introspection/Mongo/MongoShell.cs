namespace Bengine.Introspection.Mongo;

using System.Text;
using System.Text.RegularExpressions;
using Bengine.Introspection.Sql;
using MongoDB.Bson;

/// <summary>One method call of a shell command, with its arguments read as BSON.</summary>
/// <param name="Name">The method name.</param>
/// <param name="Arguments">The arguments.</param>
public sealed record ShellCall(string Name, IReadOnlyList<BsonValue> Arguments)
{
    /// <summary>Gets an argument, or <c>null</c> when it was not given.</summary>
    /// <param name="index">The argument position.</param>
    /// <returns>The argument.</returns>
    public BsonValue? Argument(int index) => index < Arguments.Count ? Arguments[index] : null;

    /// <summary>Gets a document argument; a missing or <c>null</c> one is an empty document.</summary>
    /// <param name="index">The argument position.</param>
    /// <returns>The document.</returns>
    /// <exception cref="IntrospectionException">The argument is not a document.</exception>
    public BsonDocument Document(int index) => Argument(index) switch
    {
        null or BsonNull or BsonUndefined => [],
        BsonDocument document => document,
        var other => throw Wrong(index, "a document", other),
    };

    /// <summary>Gets an optional document argument.</summary>
    /// <param name="index">The argument position.</param>
    /// <returns>The document, or <c>null</c>.</returns>
    /// <exception cref="IntrospectionException">The argument is given but not a document.</exception>
    public BsonDocument? OptionalDocument(int index) => Argument(index) is null or BsonNull or BsonUndefined ? null : Document(index);

    /// <summary>Gets an array argument.</summary>
    /// <param name="index">The argument position.</param>
    /// <returns>The array.</returns>
    /// <exception cref="IntrospectionException">The argument is not an array.</exception>
    public BsonArray Array(int index) => Argument(index) switch
    {
        BsonArray array => array,
        var other => throw Wrong(index, "an array", other),
    };

    /// <summary>Gets a string argument.</summary>
    /// <param name="index">The argument position.</param>
    /// <returns>The string.</returns>
    /// <exception cref="IntrospectionException">The argument is not a string.</exception>
    public string String(int index) => Argument(index) switch
    {
        BsonString text => text.Value,
        var other => throw Wrong(index, "a string", other),
    };

    /// <summary>Gets an integer argument.</summary>
    /// <param name="index">The argument position.</param>
    /// <returns>The number.</returns>
    /// <exception cref="IntrospectionException">The argument is not a number.</exception>
    public long Number(int index) => Argument(index) switch
    {
        { IsNumeric: true } number => number.ToInt64(),
        var other => throw Wrong(index, "a number", other),
    };

    private IntrospectionException Wrong(int index, string expected, BsonValue? actual) =>
        new($"{Name}(): argument {index + 1} must be {expected}" + (actual is null ? ", but it is missing." : $", not {MongoValues.TypeName(actual)}."));
}

/// <summary>What a shell command addresses.</summary>
public enum ShellTarget
{
    /// <summary><c>show dbs</c>, <c>show collections</c>, …</summary>
    Show,

    /// <summary><c>use name</c>.</summary>
    Use,

    /// <summary>A bare command document, run with <c>runCommand</c>.</summary>
    Command,

    /// <summary>A database method: <c>db.stats()</c>, <c>db.runCommand(…)</c>, …</summary>
    Database,

    /// <summary>A collection method: <c>db.users.find(…)</c>, …</summary>
    Collection,
}

/// <summary>A parsed shell command.</summary>
public sealed record ShellCommand
{
    /// <summary>Gets what the command addresses.</summary>
    public required ShellTarget Target { get; init; }

    /// <summary>Gets the word after <c>show</c> or <c>use</c>.</summary>
    public string? Word { get; init; }

    /// <summary>Gets the database named by <c>db.getSiblingDB(…)</c>, or <c>null</c> for the session's.</summary>
    public string? Database { get; init; }

    /// <summary>Gets the collection, for a collection method.</summary>
    public string? Collection { get; init; }

    /// <summary>Gets the command document, for a bare command.</summary>
    public BsonDocument? Command { get; init; }

    /// <summary>Gets the method calls: the database or collection method, then cursor methods.</summary>
    public IReadOnlyList<ShellCall> Calls { get; init; } = [];

    /// <summary>Gets the database or collection method.</summary>
    public ShellCall? Method => Calls.Count > 0 ? Calls[0] : null;
}

/// <summary>
/// Reads the MongoDB shell language the console speaks — the subset of
/// <c>mongosh</c> that is data rather than JavaScript: <c>show dbs</c>,
/// <c>db.coll.find({…}).sort({…}).limit(n)</c>, <c>db.coll.aggregate([…])</c>,
/// write and index methods, <c>db.runCommand({…})</c>, and bare command
/// documents. Arguments are shell values (relaxed JSON with
/// <c>ObjectId(…)</c>, <c>ISODate(…)</c>, regular expressions, …). It splits
/// scripts into commands, parses one, and classifies it for the console's
/// read-only and safety checks.
/// </summary>
public static partial class MongoShell
{
    private static readonly HashSet<string> CollectionReads = new(StringComparer.Ordinal)
    {
        "find", "findOne", "aggregate", "countDocuments", "estimatedDocumentCount", "count", "distinct", "getIndexes",
        "getIndexKeys", "indexes", "stats", "explain", "validate", "dataSize", "storageSize", "totalIndexSize", "isCapped", "latencyStats",
    };

    private static readonly HashSet<string> CollectionWrites = new(StringComparer.Ordinal)
    {
        "insertOne", "insertMany", "insert", "updateOne", "updateMany", "update", "replaceOne", "deleteOne", "deleteMany",
        "remove", "findOneAndUpdate", "findOneAndReplace", "findOneAndDelete", "bulkWrite",
    };

    private static readonly HashSet<string> CollectionDdl = new(StringComparer.Ordinal)
    {
        "createIndex", "createIndexes", "ensureIndex", "dropIndex", "dropIndexes", "drop", "renameCollection", "hideIndex", "unhideIndex",
    };

    private static readonly HashSet<string> DatabaseReads = new(StringComparer.Ordinal)
    {
        "getCollectionNames", "getCollectionInfos", "stats", "version", "serverStatus", "hostInfo", "buildInfo", "currentOp",
        "getUsers", "getUser", "getRoles", "getRole", "getName", "hello", "isMaster", "getProfilingStatus", "listCommands", "serverBuildInfo",
    };

    private static readonly HashSet<string> DatabaseDdl = new(StringComparer.Ordinal)
    {
        "createCollection", "createView", "dropDatabase", "createUser", "dropUser", "updateUser", "grantRolesToUser", "revokeRolesFromUser",
    };

    // Commands that only read (anything else may write).
    private static readonly HashSet<string> ReadCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ping", "hello", "isMaster", "ismaster", "buildInfo", "buildinfo", "serverStatus", "hostInfo", "listDatabases", "listCollections",
        "listIndexes", "dbStats", "collStats", "dataSize", "find", "count", "distinct", "explain", "connectionStatus", "getParameter",
        "usersInfo", "rolesInfo", "currentOp", "getLog", "top", "replSetGetStatus", "replSetGetConfig", "getCmdLineOpts", "listCommands",
        "validate", "aggregate", "getMore", "killCursors", "whatsmyuri", "features", "connPoolStats", "lockInfo", "getDefaultRWConcern",
    };

    private static readonly HashSet<string> DmlCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert", "update", "delete", "findAndModify", "bulkWrite",
    };

    private static readonly HashSet<string> DdlCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "create", "drop", "dropDatabase", "createIndexes", "dropIndexes", "renameCollection", "collMod", "createUser", "dropUser",
        "updateUser", "grantRolesToUser", "revokeRolesFromUser", "createRole", "dropRole", "convertToCapped", "cloneCollectionAsCapped",
    };

    /// <summary>Splits a script into commands: on <c>;</c>, and at line ends outside brackets unless the next line continues the chain (<c>.sort(…)</c>).</summary>
    /// <param name="script">The script.</param>
    /// <returns>The non-empty commands in order.</returns>
    public static IReadOnlyList<SqlStatement> Split(string script)
    {
        var statements = new List<SqlStatement>();
        var depth = 0;
        var start = 0;
        var last = '\0';

        void Flush(int end)
        {
            var s = start;
            var e = end;

            // A command starts at its code, after leading blanks and comments.
            while (s < e)
            {
                if (char.IsWhiteSpace(script[s]))
                {
                    s++;
                }
                else if (script[s] == '/' && s + 1 < e && script[s + 1] == '/')
                {
                    s = script.IndexOf('\n', s) is var nl and >= 0 && nl < e ? nl : e;
                }
                else if (script[s] == '/' && s + 1 < e && script[s + 1] == '*')
                {
                    s = script.IndexOf("*/", s + 2, StringComparison.Ordinal) is var close and >= 0 && close + 2 <= e ? close + 2 : e;
                }
                else
                {
                    break;
                }
            }

            while (e > s && char.IsWhiteSpace(script[e - 1]))
            {
                e--;
            }

            if (e > s && StripComments(script[s..e]).Trim().Length > 0)
            {
                statements.Add(new SqlStatement(script[s..e], s, e));
            }

            depth = 0;
            last = '\0';
        }

        for (var i = 0; i < script.Length;)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';
            if (c is '"' or '\'' or '`')
            {
                i = SkipString(script, i);
                last = c;
                continue;
            }

            if (c == '/' && next == '/')
            {
                i = script.IndexOf('\n', i) is var nl and >= 0 ? nl : script.Length;
                continue;
            }

            if (c == '/' && next == '*')
            {
                i = script.IndexOf("*/", i + 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : script.Length;
                continue;
            }

            if (c == '/' && RegexMayStart(last))
            {
                i = SkipRegex(script, i);
                last = '/';
                continue;
            }

            switch (c)
            {
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ';' when depth == 0:
                    Flush(i);
                    start = i + 1;
                    i++;
                    continue;
                case '\n' when depth == 0 && last != '\0' && !".,([{:+-*/=&|?!".Contains(last) && NextSignificant(script, i + 1) != '.':
                    Flush(i);
                    start = i + 1;
                    i++;
                    continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                last = c;
            }

            i++;
        }

        Flush(script.Length);
        return statements;
    }

    /// <summary>Parses one command.</summary>
    /// <param name="text">The command text.</param>
    /// <returns>The command.</returns>
    /// <exception cref="IntrospectionException">The text is not a command the console understands.</exception>
    public static ShellCommand Parse(string text)
    {
        var source = StripComments(text).Trim();
        while (source.EndsWith(';'))
        {
            source = source[..^1].TrimEnd();
        }

        if (source.Length == 0)
        {
            throw new IntrospectionException("Nothing to run.");
        }

        if (ShowOrUse().Match(source) is { Success: true } directive)
        {
            return new ShellCommand
            {
                Target = directive.Groups[1].Value == "show" ? ShellTarget.Show : ShellTarget.Use,
                Word = directive.Groups[2].Value,
            };
        }

        if (source[0] == '{')
        {
            return MongoValues.ParseValue(source) is BsonDocument command
                ? new ShellCommand { Target = ShellTarget.Command, Command = command }
                : throw new IntrospectionException("A command must be one document, for example { ping: 1 }.");
        }

        var parser = new Parser(source);
        if (parser.Identifier() != "db")
        {
            throw new IntrospectionException(
                "Start with db (db.users.find(), db.stats(), db.runCommand({…})), show dbs / show collections, or a command document such as { ping: 1 }.");
        }

        string? database = null;
        string? collection = null;
        var names = new List<string>();
        var calls = new List<ShellCall>();
        while (!parser.AtEnd)
        {
            string name;
            string? arguments = null;
            if (parser.TryRead('.'))
            {
                name = parser.Identifier() ?? throw new IntrospectionException($"Expected a name after '.' at position {parser.Position + 1}.");
                if (parser.Peek('('))
                {
                    arguments = parser.Parenthesized();
                }
            }
            else if (parser.TryRead('['))
            {
                name = parser.StringLiteral();
                if (!parser.TryRead(']'))
                {
                    throw new IntrospectionException($"Expected ']' at position {parser.Position + 1}.");
                }
            }
            else
            {
                throw new IntrospectionException($"Unexpected '{parser.Current}' at position {parser.Position + 1}.");
            }

            if (arguments is null)
            {
                if (calls.Count > 0)
                {
                    throw new IntrospectionException($"'.{name}' after a method call is JavaScript; the console runs shell commands only.");
                }

                names.Add(name);
                continue;
            }

            var parsed = ParseArguments(arguments, name);
            if (calls.Count == 0 && collection is null && names.Count == 0 && name is "getSiblingDB" or "getSisterDB")
            {
                database = new ShellCall(name, parsed).String(0);
            }
            else if (calls.Count == 0 && collection is null && names.Count == 0 && name == "getCollection")
            {
                collection = new ShellCall(name, parsed).String(0);
            }
            else
            {
                if (calls.Count == 0 && names.Count > 0)
                {
                    collection = collection is null ? string.Join('.', names) : collection + "." + string.Join('.', names);
                    names.Clear();
                }

                calls.Add(new ShellCall(name, parsed));
            }
        }

        if (names.Count > 0)
        {
            // db.users alone browses the collection.
            collection = collection is null ? string.Join('.', names) : collection + "." + string.Join('.', names);
        }

        if (collection is not null && calls.Count == 0)
        {
            calls.Add(new ShellCall("find", []));
        }

        if (calls.Count == 0)
        {
            throw new IntrospectionException("Call a method, for example db.users.find() or db.stats().");
        }

        return new ShellCommand
        {
            Target = collection is null ? ShellTarget.Database : ShellTarget.Collection,
            Database = database,
            Collection = collection,
            Calls = calls,
        };
    }

    /// <summary>Classifies a command for the console's checks: whether it can write, and whether it is an unfiltered <c>updateMany</c>/<c>deleteMany</c>.</summary>
    /// <param name="text">The command text.</param>
    /// <returns>The classification; a command that does not parse is "other" (running it reports the error).</returns>
    public static SqlStatementInfo Classify(string text)
    {
        try
        {
            return Classify(Parse(text));
        }
        catch (IntrospectionException)
        {
            return Info(SqlStatementKind.Other, string.Empty, canModify: false);
        }
    }

    /// <summary>Classifies a parsed command.</summary>
    /// <param name="command">The command.</param>
    /// <returns>The classification.</returns>
    public static SqlStatementInfo Classify(ShellCommand command)
    {
        switch (command.Target)
        {
            case ShellTarget.Show:
                return Info(SqlStatementKind.Query, "show", canModify: false);
            case ShellTarget.Use:
                return Info(SqlStatementKind.Session, "use", canModify: false);
            case ShellTarget.Command:
                return ClassifyCommand(command.Command!);
        }

        var method = command.Method!;
        if (command.Target == ShellTarget.Database)
        {
            return method.Name switch
            {
                "runCommand" or "adminCommand" when method.Argument(0) is BsonDocument inner => ClassifyCommand(inner),
                "runCommand" or "adminCommand" => Info(SqlStatementKind.Query, method.Name, canModify: false),
                "aggregate" => Info(SqlStatementKind.Query, method.Name, Writes(method.Argument(0))),
                _ when DatabaseReads.Contains(method.Name) => Info(SqlStatementKind.Query, method.Name, canModify: false),
                _ when DatabaseDdl.Contains(method.Name) => Info(SqlStatementKind.Ddl, method.Name, canModify: true),
                _ => Info(SqlStatementKind.Other, method.Name, canModify: true),
            };
        }

        // db.coll.explain().find(…) only explains.
        if (method.Name == "explain" || command.Calls.Skip(1).Any(c => c.Name == "explain"))
        {
            return Info(SqlStatementKind.Query, method.Name, canModify: false);
        }

        if (method.Name == "aggregate")
        {
            return Info(SqlStatementKind.Query, method.Name, Writes(method.Argument(0)));
        }

        if (CollectionReads.Contains(method.Name))
        {
            return Info(SqlStatementKind.Query, method.Name, canModify: false);
        }

        if (CollectionWrites.Contains(method.Name))
        {
            var unfiltered = method.Name is "updateMany" or "deleteMany" or "remove"
                && method.Argument(0) is null or BsonDocument { ElementCount: 0 };
            return Info(SqlStatementKind.Dml, method.Name, canModify: true) with { IsUnfilteredDml = unfiltered };
        }

        return CollectionDdl.Contains(method.Name)
            ? Info(SqlStatementKind.Ddl, method.Name, canModify: true)
            : Info(SqlStatementKind.Other, method.Name, canModify: true);
    }

    /// <summary>Gets the shell expression for a collection: <c>db.users</c>, or <c>db.getCollection("my-coll")</c> when the name is not an identifier.</summary>
    /// <param name="name">The collection name.</param>
    /// <returns>The expression.</returns>
    public static string Collection(string name) =>
        CollectionName().IsMatch(name) && !IsDatabaseMethod(name) ? $"db.{name}" : $"db.getCollection({MongoValues.Quote(name)})";

    /// <summary>Removes <c>//</c> and <c>/* */</c> comments outside strings and regular expressions (each comment becomes one space).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text without comments.</returns>
    public static string StripComments(string text)
    {
        if (!text.Contains('/'))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var last = '\0';
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c is '"' or '\'' or '`')
            {
                var end = SkipString(text, i);
                result.Append(text, i, end - i);
                i = end;
                last = c;
            }
            else if (c == '/' && next == '/')
            {
                i = text.IndexOf('\n', i) is var nl and >= 0 ? nl : text.Length;
                result.Append(' ');
            }
            else if (c == '/' && next == '*')
            {
                i = text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : text.Length;
                result.Append(' ');
            }
            else if (c == '/' && RegexMayStart(last))
            {
                var end = SkipRegex(text, i);
                result.Append(text, i, end - i);
                i = end;
                last = '/';
            }
            else
            {
                result.Append(c);
                if (!char.IsWhiteSpace(c))
                {
                    last = c;
                }

                i++;
            }
        }

        return result.ToString();
    }

    private static bool IsDatabaseMethod(string name) =>
        DatabaseReads.Contains(name) || DatabaseDdl.Contains(name) || name is "getCollection" or "getSiblingDB" or "runCommand" or "adminCommand" or "aggregate";

    private static SqlStatementInfo ClassifyCommand(BsonDocument command)
    {
        var name = command.ElementCount > 0 ? command.GetElement(0).Name : string.Empty;
        if (name.Equals("aggregate", StringComparison.OrdinalIgnoreCase))
        {
            return Info(SqlStatementKind.Query, name, Writes(command.GetValue("pipeline", BsonNull.Value)));
        }

        if (ReadCommands.Contains(name))
        {
            return Info(SqlStatementKind.Query, name, canModify: false);
        }

        if (DmlCommands.Contains(name))
        {
            var unfiltered = name.Equals("delete", StringComparison.OrdinalIgnoreCase) && command.GetValue("deletes", BsonNull.Value) is BsonArray deletes
                && deletes.Any(d => d is BsonDocument { } entry && entry.GetValue("q", BsonNull.Value) is BsonDocument { ElementCount: 0 } && entry.GetValue("limit", 0).ToInt32() == 0);
            return Info(SqlStatementKind.Dml, name, canModify: true) with { IsUnfilteredDml = unfiltered };
        }

        return DdlCommands.Contains(name)
            ? Info(SqlStatementKind.Ddl, name, canModify: true)
            : Info(SqlStatementKind.Other, name, canModify: true);
    }

    // A pipeline writes when a stage is $out or $merge.
    private static bool Writes(BsonValue? pipeline) =>
        pipeline is BsonArray stages && stages.OfType<BsonDocument>().Any(s => s.Contains("$out") || s.Contains("$merge"));

    private static SqlStatementInfo Info(SqlStatementKind kind, string verb, bool canModify) =>
        new() { Kind = kind, Verb = verb, CanModify = canModify, IsUnfilteredDml = false };

    private static IReadOnlyList<BsonValue> ParseArguments(string text, string method)
    {
        var values = new List<BsonValue>();
        foreach (var piece in SplitArguments(text))
        {
            try
            {
                values.Add(MongoValues.ParseValue(piece));
            }
            catch (IntrospectionException ex)
            {
                throw new IntrospectionException($"{method}(): {ex.Message}", ex);
            }
        }

        return values;
    }

    // Splits an argument list at top-level commas.
    private static List<string> SplitArguments(string text)
    {
        var pieces = new List<string>();
        var depth = 0;
        var start = 0;
        var last = '\0';
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c is '"' or '\'' or '`')
            {
                i = SkipString(text, i);
                last = c;
                continue;
            }

            if (c == '/' && RegexMayStart(last))
            {
                i = SkipRegex(text, i);
                last = '/';
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                pieces.Add(text[start..i].Trim());
                start = i + 1;
            }

            if (!char.IsWhiteSpace(c))
            {
                last = c;
            }

            i++;
        }

        // A trailing comma leaves an empty last piece, which is dropped.
        pieces.Add(text[start..].Trim());
        return pieces.Where(p => p.Length > 0).ToList();
    }

    private static char NextSignificant(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i = text.IndexOf('\n', i) is var nl and >= 0 ? nl : text.Length;
                continue;
            }

            return text[i];
        }

        return '\0';
    }

    // A '/' starts a regular expression where a value is expected, not after an operand.
    private static bool RegexMayStart(char previous) => previous == '\0' || "(,:[{=!&|?;+-*%<>~^".Contains(previous);

    private static int SkipString(string text, int start)
    {
        var quote = text[start];
        var i = start + 1;
        while (i < text.Length && text[i] != quote)
        {
            i += text[i] == '\\' ? 2 : 1;
        }

        return Math.Min(i + 1, text.Length);
    }

    private static int SkipRegex(string text, int start)
    {
        var i = start + 1;
        var inClass = false;
        while (i < text.Length && text[i] != '\n' && (text[i] != '/' || inClass))
        {
            if (text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == '[')
            {
                inClass = true;
            }
            else if (text[i] == ']')
            {
                inClass = false;
            }

            i++;
        }

        i = Math.Min(i + 1, text.Length);
        while (i < text.Length && char.IsLetter(text[i]))
        {
            i++;
        }

        return i;
    }

    [GeneratedRegex(@"^(show|use)\s+([A-Za-z0-9_$.\-]+)$")]
    private static partial Regex ShowOrUse();

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex CollectionName();

    // Reads db.x.y(…)[…] one token at a time.
    private sealed class Parser(string text)
    {
        private int _position;

        public int Position => _position;

        public bool AtEnd
        {
            get
            {
                SkipSpace();
                return _position >= text.Length;
            }
        }

        public char Current => _position < text.Length ? text[_position] : '\0';

        public bool Peek(char c)
        {
            SkipSpace();
            return _position < text.Length && text[_position] == c;
        }

        public bool TryRead(char c)
        {
            if (!Peek(c))
            {
                return false;
            }

            _position++;
            return true;
        }

        public string? Identifier()
        {
            SkipSpace();
            var start = _position;
            while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] is '_' or '$'))
            {
                _position++;
            }

            return _position > start && !char.IsDigit(text[start]) ? text[start.._position] : null;
        }

        public string StringLiteral()
        {
            SkipSpace();
            if (Current is not ('"' or '\''))
            {
                throw new IntrospectionException($"Expected a quoted name at position {_position + 1}.");
            }

            var end = SkipString(text, _position);
            var value = MongoValues.ParseValue(text[_position..end]);
            _position = end;
            return value.AsString;
        }

        // The text between '(' and its matching ')'.
        public string Parenthesized()
        {
            SkipSpace();
            var open = _position;
            var depth = 0;
            var last = '\0';
            for (var i = open; i < text.Length;)
            {
                var c = text[i];
                if (c is '"' or '\'' or '`')
                {
                    i = SkipString(text, i);
                    last = c;
                    continue;
                }

                if (c == '/' && RegexMayStart(last))
                {
                    i = SkipRegex(text, i);
                    last = '/';
                    continue;
                }

                if (c is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (c is ')' or ']' or '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (c != ')')
                        {
                            break;
                        }

                        _position = i + 1;
                        return text[(open + 1)..i];
                    }
                }

                if (!char.IsWhiteSpace(c))
                {
                    last = c;
                }

                i++;
            }

            throw new IntrospectionException($"Unbalanced brackets in the call at position {open + 1}.");
        }

        private void SkipSpace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position]))
            {
                _position++;
            }
        }
    }
}
