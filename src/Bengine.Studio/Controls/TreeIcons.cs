namespace Bengine.Studio.Controls;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Bengine.Enums;

/// <summary>
/// Builds the 16×16 object-explorer icons as Avalonia shapes — the same set the
/// web explorer draws as SVG — so every node kind is recognizable at a glance.
/// Folder kinds use their item's icon (<c>TablesFolder</c> → table), as pgAdmin
/// does.
/// </summary>
public static class TreeIcons
{
    private static readonly IBrush Ink = Brush("#0e131b");

    /// <summary>Maps a folder kind to the item kind whose icon it shows.</summary>
    /// <param name="kind">The node kind name.</param>
    /// <returns>The item kind name.</returns>
    public static string ItemKindOf(string kind)
    {
        if (kind == "Folder" || !kind.EndsWith("Folder", StringComparison.Ordinal))
        {
            return kind;
        }

        var baseName = kind[..^"Folder".Length];
        return baseName switch
        {
            "Roles" => "GroupRole",
            "Constraints" => "Constraint",
            _ when baseName.EndsWith("Indexes", StringComparison.Ordinal) => "Index",
            _ when baseName.EndsWith('s') => baseName[..^1],
            _ => baseName,
        };
    }

    /// <summary>Creates the icon for a node kind.</summary>
    /// <param name="kind">The node kind name (or <c>ServerGroup</c>).</param>
    /// <param name="provider">The server's provider, which colors server icons.</param>
    /// <param name="connected">Whether a server or database is connected; disconnected ones are grey.</param>
    /// <param name="badge">An optional badge (<c>PK</c>/<c>FK</c>) for column icons.</param>
    /// <returns>A 16×16 control.</returns>
    public static Control Create(string kind, DatabaseProvider provider = DatabaseProvider.PostgreSql, bool connected = true, string? badge = null)
    {
        var c = new Canvas { Width = 16, Height = 16, ClipToBounds = false };
        switch (ItemKindOf(kind))
        {
            case "ServerGroup":
                Rect(c, 1, 2, 9, 4, 1, "#8a9bb3");
                Rect(c, 1, 7, 9, 4, 1, "#8a9bb3");
                Rect(c, 6, 9, 9, 5, 1, "#b6c3d6");
                break;
            case "Package":
                Rect(c, 2.5, 4, 11, 9.5, 1.5, "#d4a24c");
                Line(c, "M2.5 7.5 L13.5 7.5 M8 4 L8 13.5", "#8a6420", 1.2);
                break;
            case "Synonym":
                Line(c, "M3 6 L12 6 M9.5 3.5 L12 6 L9.5 8.5 M13 10 L4 10 M6.5 7.5 L4 10 L6.5 12.5", "#7fb4e0", 1.5);
                break;
            case "Folder":
                PathFill(c, "M1.5 3.5 L6 3.5 L7.5 5 L14.5 5 L14.5 13 L1.5 13 Z", "#7f8ea3");
                PathFill(c, "M1.5 6.5 L14.5 6.5 L14.5 13 L1.5 13 Z", "#9aa8bb");
                break;
            case "Server":
                Rack(c, connected ? ProviderColor(provider) : "#5b6576");
                if (!connected)
                {
                    Circle(c, 12.4, 12.4, 3.3, "#e5484d", "#151b26", 1);
                    Line(c, "M11 11 L13.8 13.8 M13.8 11 L11 13.8", "#ffffff", 1.2);
                }

                break;
            case "Database":
                PathFill(c, "M2.5 4 V12 C2.5 13.2 5 14.2 8 14.2 C11 14.2 13.5 13.2 13.5 12 V4 Z", connected ? "#c9973a" : "#4f5868");
                Ellipse(c, 8, 4, 5.5, 2.2, connected ? "#f0c060" : "#6b7686");
                Line(c, "M2.5 8.1 C2.5 9.3 5 10.3 8 10.3 C11 10.3 13.5 9.3 13.5 8.1", connected ? "#f0c060" : "#6b7686", 0.9);
                break;
            case "Schema":
                PathFill(c, "M8 1.5 L14.5 8 L8 14.5 L1.5 8 Z", "#e06c75");
                PathFill(c, "M8 5 L11 8 L8 11 L5 8 Z", "#f4a4ab");
                break;
            case "Catalog":
                PathFill(c, "M8 1.5 L14.5 8 L8 14.5 L1.5 8 Z", "#b48ead");
                PathFill(c, "M8 5 L11 8 L8 11 L5 8 Z", "#d8bfd8");
                break;
            case "Table":
                Grid(c, "#6ea8fe");
                break;
            case "View":
                Grid(c, "#5fd38d");
                break;
            case "MaterializedView":
                Grid(c, "#c792ea");
                break;
            case "ForeignTable":
                Grid(c, "#f0a35e");
                break;
            case "Column":
                Rect(c, 4.5, 1.5, 7, 13, 1.4, "#17202d", "#8fb3d9");
                Rect(c, 4.5, 1.5, 7, 3.2, 1.4, "#8fb3d9");
                if (badge is "PK" or "FK")
                {
                    Circle(c, 12.6, 12.6, 2.6, badge == "PK" ? "#e5c07b" : "#61afef", "#151b26", 1);
                }

                break;
            case "Constraint":
                Key(c, "#b9a26b");
                break;
            case "PrimaryKey":
                Key(c, "#e5c07b");
                break;
            case "ForeignKey":
                Key(c, "#61afef");
                break;
            case "UniqueConstraint":
                Glyph(c, "U", "#56b6c2");
                break;
            case "CheckConstraint":
                Line(c, "M2.5 8.5 L6 12 L13.5 4", "#98c379", 2.2);
                break;
            case "ExclusionConstraint":
                Circle(c, 8, 8, 5.5, null, "#e06c75", 1.8);
                Line(c, "M4.2 11.8 L11.8 4.2", "#e06c75", 1.8);
                break;
            case "Index":
                PathFill(c, "M4 1.5 H12 V14.5 L8 11.3 L4 14.5 Z", "#a78bfa");
                break;
            case "Trigger":
                PathFill(c, "M9.5 1 L3 9.2 H7.3 L6.3 15 L13 6.6 H8.6 Z", "#f5a623");
                break;
            case "Function":
                Glyph(c, "fx", "#3fb6a8");
                break;
            case "TriggerFunction":
                Glyph(c, "fx", "#f5a623");
                break;
            case "Procedure":
                Glyph(c, "P", "#61afef");
                break;
            case "Aggregate":
                Glyph(c, "Σ", "#d19a66", 9);
                break;
            case "Sequence":
                Glyph(c, "123", "#c678dd", 6);
                break;
            case "Type":
                Glyph(c, "T", "#56b6c2");
                break;
            case "Domain":
                Glyph(c, "D", "#98c379");
                break;
            case "Collation":
                Glyph(c, "Aa", "#abb2bf", 7);
                break;
            case "Language":
                Glyph(c, "{}", "#e5c07b");
                break;
            case "Event":
                Circle(c, 8, 8, 6, null, "#e0af68", 1.7);
                Line(c, "M8 4.5 V8 L10.5 9.6", "#e0af68", 1.7);
                break;
            case "Extension":
                PathFill(c, "M2.5 5.5 H5.5 A1.6 1.6 0 1 1 8.7 5.5 H11.7 V8.5 A1.6 1.6 0 1 0 11.7 11.7 V14.7 H2.5 Z", "#8bc34a");
                break;
            case "Cast":
                Line(c, "M2 5.2 H12.5 M10 2.7 L12.5 5.2 L10 7.7 M14 10.8 H3.5 M6 8.3 L3.5 10.8 L6 13.3", "#26a69a", 1.6);
                break;
            case "EventTrigger":
                Circle(c, 8, 8, 6.3, null, "#f78c6c", 1.4);
                PathFill(c, "M8.8 3.6 L5.2 8.6 H7.7 L7.1 12.4 L10.8 7.3 H8.3 Z", "#f78c6c");
                break;
            case "ForeignDataWrapper":
                Line(c, "M5.5 1.5 V5.5 M10.5 1.5 V5.5 M8 12.2 V14.7", "#ff9e64", 1.6);
                PathFill(c, "M3.5 5.5 H12.5 V7.7 A4.5 4.5 0 0 1 3.5 7.7 Z", "#ff9e64");
                break;
            case "ForeignServer":
                Rack(c, "#ff9e64");
                break;
            case "Publication":
                Circle(c, 8, 9, 1.8, "#7dcfff", null, 0);
                Line(c, "M4.8 12.2 A4.5 4.5 0 0 1 4.8 5.8 M11.2 5.8 A4.5 4.5 0 0 1 11.2 12.2", "#7dcfff", 1.3);
                break;
            case "Subscription":
                PathFill(c, "M1.5 9.5 H5.5 L6.5 11.5 H9.5 L10.5 9.5 H14.5 V14 H1.5 Z", "#7dcfff");
                Line(c, "M8 1.5 V8 M5.5 5.5 L8 8 L10.5 5.5", "#7dcfff", 1.6);
                break;
            case "LoginRole":
                Circle(c, 8, 5, 3, "#61afef", null, 0);
                PathFill(c, "M2.5 14.5 A5.5 5 0 0 1 13.5 14.5 Z", "#61afef");
                break;
            case "GroupRole":
                Circle(c, 5.5, 5.5, 2.4, "#9aa5ce", null, 0);
                Circle(c, 11, 5, 2.4, "#c0caf5", null, 0);
                PathFill(c, "M1 14.5 A4.5 4.2 0 0 1 10 14.5 Z M6.5 14 A4.5 4.2 0 0 1 15.5 14 Z", "#9aa5ce");
                break;
            case "Tablespace":
                PathFill(c, "M8 2 L14.5 5 L8 8 L1.5 5 Z", "#a9b1d6");
                Line(c, "M1.5 8 L8 11 L14.5 8 M1.5 11 L8 14 L14.5 11", "#a9b1d6", 1.4);
                break;
            default:
                Rect(c, 2.5, 2.5, 11, 11, 2, "#5b6576");
                break;
        }

        return c;
    }

    private static string ProviderColor(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.MySql => "#f29111",
        DatabaseProvider.SqlServer => "#e5484d",
        DatabaseProvider.Sqlite => "#4fc1e9",
        DatabaseProvider.Oracle => "#c74634",
        _ => "#5b9bd5",
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static void Place(Canvas canvas, Control child, double left, double top)
    {
        Canvas.SetLeft(child, left);
        Canvas.SetTop(child, top);
        canvas.Children.Add(child);
    }

    private static void Rect(Canvas c, double x, double y, double w, double h, double r, string? fill, string? stroke = null) =>
        Place(c, new Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = r,
            RadiusY = r,
            Fill = fill is null ? null : Brush(fill),
            Stroke = stroke is null ? null : Brush(stroke),
            StrokeThickness = stroke is null ? 0 : 1,
        }, x, y);

    private static void Circle(Canvas c, double cx, double cy, double r, string? fill, string? stroke, double thickness) =>
        Place(c, new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Fill = fill is null ? null : Brush(fill),
            Stroke = stroke is null ? null : Brush(stroke),
            StrokeThickness = thickness,
        }, cx - r, cy - r);

    private static void Ellipse(Canvas c, double cx, double cy, double rx, double ry, string fill) =>
        Place(c, new Ellipse { Width = rx * 2, Height = ry * 2, Fill = Brush(fill) }, cx - rx, cy - ry);

    private static void PathFill(Canvas c, string data, string fill) =>
        Place(c, new Path { Data = Geometry.Parse(data), Fill = Brush(fill) }, 0, 0);

    private static void Line(Canvas c, string data, string stroke, double thickness) =>
        Place(c, new Path
        {
            Data = Geometry.Parse(data),
            Stroke = Brush(stroke),
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        }, 0, 0);

    private static void Grid(Canvas c, string color)
    {
        Rect(c, 1.5, 2.5, 13, 11, 1.6, "#17202d", color);
        Rect(c, 1.5, 2.5, 13, 3.2, 1.6, color);
        Place(c, new Path
        {
            Data = Geometry.Parse("M1.8 9.3 H14.2 M5.9 5.7 V13.3 M10.1 5.7 V13.3"),
            Stroke = Brush(color),
            StrokeThickness = 0.9,
            Opacity = 0.75,
        }, 0, 0);
    }

    private static void Rack(Canvas c, string color)
    {
        Rect(c, 1.5, 1.8, 13, 5.2, 1.2, color);
        Rect(c, 1.5, 9, 13, 5.2, 1.2, color);
        Circle(c, 4.2, 4.4, 0.95, "#0e131b", null, 0);
        Circle(c, 4.2, 11.6, 0.95, "#0e131b", null, 0);
        Place(c, new Path { Data = Geometry.Parse("M7 4.4 H12.2 M7 11.6 H12.2"), Stroke = Ink, StrokeThickness = 1.1, Opacity = 0.55 }, 0, 0);
    }

    private static void Key(Canvas c, string color)
    {
        Circle(c, 5, 8, 3, null, color, 1.9);
        Line(c, "M8 8 H14.3 M11.8 8 V10.6 M14 8 V10.1", color, 1.9);
    }

    private static void Glyph(Canvas c, string text, string color, double size = 8) =>
        Place(c, new Border
        {
            Width = 14,
            Height = 12,
            CornerRadius = new CornerRadius(3),
            Background = Brush(color),
            Child = new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = FontWeight.Bold,
                Foreground = Ink,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        }, 1, 2);
}
