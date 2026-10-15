namespace Bengine.Studio.Controls;

using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

/// <summary>
/// File and folder icons from the Material Icon Theme (MIT, Material
/// Extensions; the SVGs and their license are in <c>Assets/Icons</c>), drawn as
/// vectors: each SVG's paths, circles, ellipses, and rectangles become an
/// Avalonia <see cref="DrawingImage"/> the size of its view box. Used for the
/// Files panel and for the object-kind folders of the database tree.
/// </summary>
public static class MaterialIcons
{
    private static readonly Dictionary<string, IImage?> Cache = new(StringComparer.Ordinal);

    // File extensions → icon, after the theme's own mapping (a subset that fits a database IDE).
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sql"] = "database", ["psql"] = "database", ["pgsql"] = "database", ["ddl"] = "database", ["dml"] = "database",
        ["db"] = "database", ["sqlite"] = "database", ["sqlite3"] = "database", ["bak"] = "database", ["dump"] = "database",
        ["json"] = "json", ["jsonl"] = "json", ["ndjson"] = "json",
        ["csv"] = "table", ["tsv"] = "table", ["xlsx"] = "table", ["xls"] = "table",
        ["md"] = "markdown", ["txt"] = "document", ["xml"] = "xml", ["yaml"] = "yaml", ["yml"] = "yaml", ["log"] = "log",
        ["sh"] = "console", ["bash"] = "console", ["ps1"] = "console", ["bat"] = "console",
        ["ini"] = "settings", ["conf"] = "settings", ["cfg"] = "settings", ["toml"] = "toml", ["env"] = "tune",
        ["js"] = "javascript", ["ts"] = "typescript", ["cs"] = "csharp", ["py"] = "python", ["go"] = "go", ["rs"] = "rust",
        ["html"] = "html", ["zip"] = "zip", ["gz"] = "zip",
    };

    // Folder names → icon.
    private static readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sql"] = "folder-database", ["db"] = "folder-database", ["database"] = "folder-database", ["databases"] = "folder-database",
        ["migrations"] = "folder-migrations", ["migration"] = "folder-migrations", ["scripts"] = "folder-scripts",
        ["backup"] = "folder-backup", ["backups"] = "folder-backup", ["dump"] = "folder-dump", ["dumps"] = "folder-dump",
        ["docs"] = "folder-docs", ["test"] = "folder-test", ["tests"] = "folder-test", ["config"] = "folder-config",
        ["scratches"] = "folder-scrap", ["seeds"] = "folder-seeders", ["seeders"] = "folder-seeders", ["queries"] = "folder-console",
        ["data"] = "folder-database", ["export"] = "folder-export", ["exports"] = "folder-export", ["import"] = "folder-import",
    };

    // Database tree folders (engine folder kinds and the layout's own folders) → icon.
    private static readonly Dictionary<string, string> TreeFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TablesFolder"] = "folder-database", ["ViewsFolder"] = "folder-views", ["MaterializedViewsFolder"] = "folder-views",
        ["ForeignTablesFolder"] = "folder-connection", ["FunctionsFolder"] = "folder-functions", ["ProceduresFolder"] = "folder-functions",
        ["TriggerFunctionsFolder"] = "folder-functions", ["AggregatesFolder"] = "folder-functions", ["routines"] = "folder-functions",
        ["SequencesFolder"] = "folder-enum", ["TypesFolder"] = "folder-interface", ["DomainsFolder"] = "folder-class",
        ["CollationsFolder"] = "folder-i18n", ["PackagesFolder"] = "folder-packages", ["SynonymsFolder"] = "folder-shared",
        ["EventsFolder"] = "folder-event", ["TriggersFolder"] = "folder-trigger", ["EventTriggersFolder"] = "folder-trigger",
        ["IndexesFolder"] = "folder-toc", ["keys"] = "folder-keys", ["foreign keys"] = "folder-link", ["checks"] = "folder-guard",
        ["exclusion constraints"] = "folder-policy", ["ConstraintsFolder"] = "folder-keys",
        ["server objects"] = "folder-server", ["database objects"] = "folder-config", ["RolesFolder"] = "folder-admin",
        ["TablespacesFolder"] = "folder-archive", ["CastsFolder"] = "folder-pipe", ["CatalogsFolder"] = "folder-core",
        ["ExtensionsFolder"] = "folder-plugin", ["ForeignDataWrappersFolder"] = "folder-connection", ["ForeignServersFolder"] = "folder-server",
        ["LanguagesFolder"] = "folder-syntax", ["PublicationsFolder"] = "folder-upload", ["SubscriptionsFolder"] = "folder-download",
        ["DatabasesFolder"] = "folder-database", ["SchemasFolder"] = "folder-database", ["ColumnsFolder"] = "folder-mappings",
    };

    /// <summary>Gets the icon of a database tree folder.</summary>
    /// <param name="kind">The folder's node kind (<c>ViewsFolder</c>) or, for the layout's own folders, its label (<c>routines</c>).</param>
    /// <param name="expanded">Whether the folder is open.</param>
    /// <returns>The image.</returns>
    public static IImage? ForTreeFolder(string kind, bool expanded) => Folder(TreeFolders.GetValueOrDefault(kind, "folder"), expanded);

    /// <summary>Lists every icon the Studio uses, so the vendored set can be checked.</summary>
    /// <returns>The icon names.</returns>
    public static IEnumerable<string> Names() =>
        Extensions.Values.Concat(["file"])
            .Concat(Folders.Values.Concat(TreeFolders.Values).Concat(["folder"]).SelectMany(f => new[] { f, f + "-open" }))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    /// <summary>Gets an icon by its theme name (<c>database</c>, <c>folder-views</c>, …).</summary>
    /// <param name="name">The icon name, the SVG's file name without extension.</param>
    /// <returns>The image, or <c>null</c> when there is no such icon.</returns>
    public static IImage? Get(string name)
    {
        if (Cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        IImage? image = null;
        var uri = new Uri($"avares://bengine-studio/Assets/Icons/{name}.svg");
        if (AssetLoader.Exists(uri))
        {
            using var stream = AssetLoader.Open(uri);
            image = Parse(XDocument.Load(stream));
        }

        Cache[name] = image;
        return image;
    }

    /// <summary>Gets the icon of a file or folder in the Files panel.</summary>
    /// <param name="name">The file or folder name.</param>
    /// <param name="isDirectory">Whether it is a folder.</param>
    /// <param name="expanded">Whether a folder is expanded.</param>
    /// <returns>The image.</returns>
    public static IImage? ForFile(string name, bool isDirectory, bool expanded = false)
    {
        if (isDirectory)
        {
            return Folder(Folders.GetValueOrDefault(name, "folder"), expanded);
        }

        var extension = Path.GetExtension(name).TrimStart('.');
        return Get(Extensions.GetValueOrDefault(extension, "file")) ?? Get("file");
    }

    /// <summary>Gets a folder icon, open or closed (<c>folder-views</c> → <c>folder-views-open</c>).</summary>
    /// <param name="name">The closed folder's icon name.</param>
    /// <param name="expanded">Whether the folder is open.</param>
    /// <returns>The image.</returns>
    public static IImage? Folder(string name, bool expanded) =>
        (expanded ? Get(name + "-open") : null) ?? Get(name) ?? Get(expanded ? "folder-open" : "folder");

    /// <summary>Converts an SVG document into a drawing the size of its view box.</summary>
    /// <param name="svg">The SVG.</param>
    /// <returns>The image.</returns>
    public static DrawingImage Parse(XDocument svg)
    {
        var root = svg.Root ?? throw new FormatException("Empty SVG.");
        var box = (Attr(root, "viewBox") ?? "0 0 16 16").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();
        var group = new DrawingGroup();

        // A transparent frame keeps the view box's margins in the image's size.
        group.Children.Add(new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(new Rect(box[0], box[1], box[2], box[3])) });
        var gradients = root.Descendants().Where(e => e.Name.LocalName is "linearGradient" or "radialGradient")
            .Where(e => Attr(e, "id") is not null)
            .ToDictionary(e => Attr(e, "id")!, e => e.Elements().FirstOrDefault(s => s.Name.LocalName == "stop") is { } stop ? Attr(stop, "stop-color") ?? Style(stop, "stop-color") : null);
        // The root's own fill (often "none") is inherited like a group's.
        Add(group, root, new Paint("#000000", 1, "nonzero"), gradients);
        return new DrawingImage(group);
    }

    private static void Add(DrawingGroup parent, XElement element, Paint inherited, IReadOnlyDictionary<string, string?> gradients)
    {
        var name = element.Name.LocalName;
        if (name is "defs" or "clipPath" or "mask" or "title" or "desc" or "linearGradient" or "radialGradient" or "style")
        {
            return;
        }

        var paint = inherited with
        {
            Fill = Attr(element, "fill") ?? Style(element, "fill") ?? inherited.Fill,
            Opacity = inherited.Opacity * Number(Attr(element, "opacity") ?? Attr(element, "fill-opacity") ?? Style(element, "opacity") ?? "1"),
            Rule = Attr(element, "fill-rule") ?? Style(element, "fill-rule") ?? inherited.Rule,
        };
        var target = parent;
        if (Attr(element, "transform") is { } transform && Transform(transform) is { } matrix)
        {
            target = new DrawingGroup { Transform = new MatrixTransform(matrix) };
            parent.Children.Add(target);
        }

        if (name == "g" || name == "svg")
        {
            foreach (var child in element.Elements())
            {
                Add(target, child, paint, gradients);
            }

            return;
        }

        Geometry? geometry = name switch
        {
            "path" when Attr(element, "d") is { } d => Geometry.Parse((paint.Rule == "evenodd" ? "F0 " : "F1 ") + d),
            "circle" => new EllipseGeometry(new Rect(
                Number(Attr(element, "cx")) - Number(Attr(element, "r")), Number(Attr(element, "cy")) - Number(Attr(element, "r")),
                2 * Number(Attr(element, "r")), 2 * Number(Attr(element, "r")))),
            "ellipse" => new EllipseGeometry(new Rect(
                Number(Attr(element, "cx")) - Number(Attr(element, "rx")), Number(Attr(element, "cy")) - Number(Attr(element, "ry")),
                2 * Number(Attr(element, "rx")), 2 * Number(Attr(element, "ry")))),
            "rect" => new RectangleGeometry(
                new Rect(Number(Attr(element, "x")), Number(Attr(element, "y")), Number(Attr(element, "width")), Number(Attr(element, "height"))),
                Number(Attr(element, "rx") ?? Attr(element, "ry")), Number(Attr(element, "ry") ?? Attr(element, "rx"))),
            _ => null,
        };
        if (geometry is null || Brush(paint, gradients) is not { } brush)
        {
            return;
        }

        target.Children.Add(new GeometryDrawing { Geometry = geometry, Brush = brush });
    }

    private static IBrush? Brush(Paint paint, IReadOnlyDictionary<string, string?> gradients)
    {
        var fill = paint.Fill;
        if (fill is null or "none")
        {
            return null;
        }

        // A gradient is drawn in its first color.
        if (fill.StartsWith("url(#", StringComparison.Ordinal))
        {
            fill = gradients.GetValueOrDefault(fill[5..^1]) ?? "#000000";
        }

        return Color.TryParse(fill, out var color) ? new SolidColorBrush(color, paint.Opacity) : null;
    }

    private static Matrix? Transform(string text)
    {
        var parts = text.Split([' ', ',', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var values = parts.Skip(1).Select(Number).ToArray();
        return parts[0] switch
        {
            "translate" => Matrix.CreateTranslation(values[0], values.Length > 1 ? values[1] : 0),
            "scale" => Matrix.CreateScale(values[0], values.Length > 1 ? values[1] : values[0]),
            "matrix" when values.Length == 6 => new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]),
            "rotate" when values.Length == 1 => Matrix.CreateRotation(values[0] * Math.PI / 180),
            _ => null,
        };
    }

    private static string? Attr(XElement element, string name) => element.Attribute(name)?.Value;

    // A property set in a style="a: b; c: d" attribute.
    private static string? Style(XElement element, string name) =>
        Attr(element, "style")?.Split(';').Select(p => p.Split(':', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim() == name)?[1].Trim();

    private static double Number(string? text) =>
        double.TryParse(text?.Replace("px", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    // The paint an element inherits from its group.
    private sealed record Paint(string? Fill, double Opacity, string Rule);
}
