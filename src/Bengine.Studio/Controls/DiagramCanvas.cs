namespace Bengine.Studio.Controls;

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Design;

/// <summary>
/// Draws an ER diagram: one box per table with its columns (key and
/// foreign-key marks, types), and a curve per foreign key from the referencing
/// columns to the referenced table, with a crow's foot at the "many" end.
/// Drag a box to move it, drag the background to pan, use the wheel to zoom
/// (around the pointer), and double-click a box to open its data.
/// </summary>
public sealed class DiagramCanvas : Control
{
    private const double HeaderHeight = 28;
    private const double RowHeight = 20;
    private const int MaxRows = 30;

    private static readonly Typeface Regular = new("Inter");
    private static readonly Typeface Bold = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0b1015"));
    private static readonly IBrush GridDot = new SolidColorBrush(Color.Parse("#16202b"));
    private static readonly IBrush BoxFill = new SolidColorBrush(Color.Parse("#161c25"));
    private static readonly IBrush HeaderFill = new SolidColorBrush(Color.Parse("#1f2a38"));
    private static readonly IBrush ViewHeaderFill = new SolidColorBrush(Color.Parse("#173226"));
    private static readonly IPen BoxPen = new Pen(new SolidColorBrush(Color.Parse("#2c3a4d")), 1);
    private static readonly IPen HotPen = new Pen(new SolidColorBrush(Color.Parse("#e0862f")), 2);
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.Parse("#4a6080")), 1.4);
    private static readonly IPen HotEdgePen = new Pen(new SolidColorBrush(Color.Parse("#e0862f")), 2);
    private static readonly IBrush NameBrush = new SolidColorBrush(Color.Parse("#e6edf6"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#c3cede"));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#5d6a7d"));
    private static readonly IBrush KeyBrush = new SolidColorBrush(Color.Parse("#e0b341"));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.Parse("#82aaff"));

    private readonly Dictionary<string, Rect> _boxes = new();
    private IReadOnlyList<CatalogRelation> _relations = [];
    private IReadOnlyList<CatalogForeignKey> _keys = [];
    private double _scale = 1;
    private Vector _offset = new(30, 30);
    private string? _dragging;
    private Point _dragStart;
    private Point _dragOrigin;
    private Vector _panOrigin;
    private bool _panning;
    private string? _hot;

    /// <summary>Raised when a table box is double-clicked.</summary>
    public event Action<CatalogRelation>? TableActivated;

    /// <summary>Raised when the zoom changes.</summary>
    public event Action<double>? ZoomChanged;

    /// <summary>Gets or sets whether only key columns are shown in each box.</summary>
    public bool KeysOnly { get; set; }

    /// <summary>Gets or sets the name of a table to emphasize (search), or <c>null</c>.</summary>
    public string? Highlight { get; set; }

    /// <summary>Gets the zoom factor.</summary>
    public double Zoom => _scale;

    /// <summary>Shows relations and their foreign keys, laid out afresh.</summary>
    /// <param name="relations">The relations.</param>
    /// <param name="keys">The foreign keys.</param>
    public void Show(IReadOnlyList<CatalogRelation> relations, IReadOnlyList<CatalogForeignKey> keys)
    {
        _relations = relations;
        _keys = keys;
        Relayout();
    }

    /// <summary>Re-runs the automatic layout.</summary>
    public void Relayout()
    {
        _boxes.Clear();
        foreach (var node in DiagramLayout.Layout(_relations, _keys, Measure))
        {
            _boxes[node.Key] = new Rect(node.X, node.Y, node.Width, node.Height);
        }

        InvalidateVisual();
    }

    /// <summary>Zooms and pans so the whole diagram fits the control.</summary>
    public void Fit()
    {
        if (_boxes.Count == 0 || Bounds.Width < 10)
        {
            return;
        }

        var all = Extent();
        _scale = Math.Clamp(Math.Min((Bounds.Width - 40) / all.Width, (Bounds.Height - 40) / all.Height), 0.15, 1.5);
        _offset = new Vector(20 - (all.X * _scale) + ((Bounds.Width - 40 - (all.Width * _scale)) / 2), 20 - (all.Y * _scale));
        ZoomChanged?.Invoke(_scale);
        InvalidateVisual();
    }

    /// <summary>Sets the zoom factor around the control's center.</summary>
    /// <param name="scale">The zoom factor.</param>
    public void SetZoom(double scale) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), Math.Clamp(scale, 0.15, 3) / _scale);

    /// <summary>Scrolls a table into view and emphasizes it.</summary>
    /// <param name="name">The table name (case-insensitive substring).</param>
    /// <returns><c>true</c> if a table matched.</returns>
    public bool Reveal(string name)
    {
        var match = _relations.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? _relations.FirstOrDefault(r => r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        Highlight = match?.Name;
        if (match is not null && _boxes.TryGetValue(DiagramLayout.KeyOf(match.Schema, match.Name), out var box))
        {
            _offset = new Vector((Bounds.Width / 2) - (box.Center.X * _scale), (Bounds.Height / 2) - (box.Center.Y * _scale));
        }

        InvalidateVisual();
        return match is not null;
    }

    /// <summary>Renders the whole diagram (not just the visible part) to a PNG.</summary>
    /// <param name="stream">The output stream.</param>
    public void SavePng(Stream stream)
    {
        var all = Extent().Inflate(30);
        var size = new PixelSize((int)Math.Ceiling(all.Width * 2), (int)Math.Ceiling(all.Height * 2));
        using var bitmap = new RenderTargetBitmap(size, new Vector(192, 192));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(Background, new Rect(0, 0, all.Width, all.Height));
            using (context.PushTransform(Matrix.CreateTranslation(-all.X, -all.Y)))
            {
                DrawContent(context);
            }
        }

        bitmap.Save(stream);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        var step = 24 * _scale;
        if (step > 8)
        {
            for (var x = _offset.X % step; x < Bounds.Width; x += step)
            {
                for (var y = _offset.Y % step; y < Bounds.Height; y += step)
                {
                    context.FillRectangle(GridDot, new Rect(x, y, 1.5, 1.5));
                }
            }
        }

        using (context.PushTransform(Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
        {
            DrawContent(context);
        }

        if (_relations.Count == 0)
        {
            var text = Text("No tables to draw.", Regular, 14, DimBrush);
            context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var world = ToWorld(point);
        var hit = HitTest(world);
        if (e.ClickCount == 2 && hit is not null)
        {
            TableActivated?.Invoke(_relations.First(r => DiagramLayout.KeyOf(r.Schema, r.Name) == hit));
            return;
        }

        _dragStart = point;
        if (hit is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = hit;
            _dragOrigin = _boxes[hit].TopLeft;
        }
        else
        {
            _panning = true;
            _panOrigin = _offset;
        }

        e.Pointer.Capture(this);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_dragging is not null)
        {
            var delta = (point - _dragStart) / _scale;
            var box = _boxes[_dragging];
            _boxes[_dragging] = new Rect(_dragOrigin + delta, box.Size);
            InvalidateVisual();
        }
        else if (_panning)
        {
            _offset = _panOrigin + (point - _dragStart);
            InvalidateVisual();
        }
        else
        {
            var hot = HitTest(ToWorld(point));
            if (hot != _hot)
            {
                _hot = hot;
                Cursor = hot is null ? Cursor.Default : new Cursor(StandardCursorType.SizeAll);
                InvalidateVisual();
            }
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = null;
        _panning = false;
        e.Pointer.Capture(null);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(e.GetPosition(this), e.Delta.Y > 0 ? 1.12 : 1 / 1.12);
        e.Handled = true;
    }

    private void ZoomAt(Point anchor, double factor)
    {
        var scale = Math.Clamp(_scale * factor, 0.15, 3);
        var world = ToWorld(anchor);
        _scale = scale;
        _offset = new Vector(anchor.X - (world.X * scale), anchor.Y - (world.Y * scale));
        ZoomChanged?.Invoke(_scale);
        InvalidateVisual();
    }

    private Point ToWorld(Point screen) => new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);

    private string? HitTest(Point world) => _boxes.LastOrDefault(b => b.Value.Contains(world)).Key;

    private Rect Extent()
    {
        var boxes = _boxes.Values.ToList();
        if (boxes.Count == 0)
        {
            return new Rect(0, 0, 100, 100);
        }

        var left = boxes.Min(b => b.Left);
        var top = boxes.Min(b => b.Top);
        return new Rect(left, top, boxes.Max(b => b.Right) - left, boxes.Max(b => b.Bottom) - top);
    }

    private IReadOnlyList<CatalogColumn> Shown(CatalogRelation relation)
    {
        var linked = _keys.Where(k => k.Table.Equals(relation.Name, StringComparison.OrdinalIgnoreCase)).SelectMany(k => k.Columns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = KeysOnly ? relation.Columns.Where(c => c.IsPrimaryKey || linked.Contains(c.Name)).ToList() : relation.Columns.ToList();
        return columns;
    }

    private (double Width, double Height) Measure(CatalogRelation relation)
    {
        var columns = Shown(relation);
        var width = Text(relation.Name, Bold, 13, NameBrush).Width + 40;
        foreach (var column in columns.Take(MaxRows))
        {
            width = Math.Max(width, Text(column.Name, Regular, 12, TextBrush).Width + Text(column.Type, Regular, 11, DimBrush).Width + 56);
        }

        var rows = Math.Min(columns.Count, MaxRows) + (columns.Count > MaxRows ? 1 : 0);
        return (Math.Clamp(width, 150, 440), HeaderHeight + (rows * RowHeight) + 8);
    }

    private void DrawContent(DrawingContext context)
    {
        var related = _hot is null ? [] : _keys.Where(k => DiagramLayout.KeyOf(k.Schema, k.Table) == _hot || DiagramLayout.KeyOf(k.ReferencedSchema, k.ReferencedTable) == _hot).ToHashSet();
        foreach (var key in _keys)
        {
            DrawEdge(context, key, related.Contains(key));
        }

        foreach (var relation in _relations)
        {
            var id = DiagramLayout.KeyOf(relation.Schema, relation.Name);
            if (_boxes.TryGetValue(id, out var box))
            {
                DrawBox(context, relation, box, id == _hot || string.Equals(relation.Name, Highlight, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    private void DrawBox(DrawingContext context, CatalogRelation relation, Rect box, bool hot)
    {
        context.DrawRectangle(BoxFill, hot ? HotPen : BoxPen, box, 6, 6);
        var header = new Rect(box.X, box.Y, box.Width, HeaderHeight);
        using (context.PushClip(new RoundedRect(box, 6)))
        {
            context.FillRectangle(relation.Kind == Bengine.Introspection.Model.TableKind.View ? ViewHeaderFill : HeaderFill, header);
        }

        var name = Text(relation.Name, Bold, 13, NameBrush);
        context.DrawText(name, new Point(box.X + 10, box.Y + ((HeaderHeight - name.Height) / 2)));
        if (relation.Schema is { } schema)
        {
            var schemaText = Text(schema, Regular, 10.5, DimBrush);
            if (name.Width + schemaText.Width + 30 < box.Width)
            {
                context.DrawText(schemaText, new Point(box.Right - schemaText.Width - 8, box.Y + ((HeaderHeight - schemaText.Height) / 2)));
            }
        }

        var linked = _keys.Where(k => k.Table.Equals(relation.Name, StringComparison.OrdinalIgnoreCase)).SelectMany(k => k.Columns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = Shown(relation);
        var y = box.Y + HeaderHeight + 4;
        foreach (var column in columns.Take(MaxRows))
        {
            var mark = column.IsPrimaryKey ? ("◆", KeyBrush) : linked.Contains(column.Name) ? ("➜", LinkBrush) : ("·", DimBrush);
            context.DrawText(Text(mark.Item1, Regular, 10, mark.Item2), new Point(box.X + 9, y + 3));
            var text = Text(column.Name, column.IsPrimaryKey ? Bold : Regular, 12, column.IsNullable ? TextBrush : NameBrush);
            context.DrawText(text, new Point(box.X + 24, y + 2));
            var type = Text(column.Type, Regular, 11, DimBrush);
            context.DrawText(type, new Point(Math.Max(box.X + 30 + text.Width, box.Right - type.Width - 10), y + 3));
            y += RowHeight;
        }

        if (columns.Count > MaxRows)
        {
            context.DrawText(Text($"… {columns.Count - MaxRows} more", Regular, 11, DimBrush), new Point(box.X + 24, y + 2));
        }
    }

    private void DrawEdge(DrawingContext context, CatalogForeignKey key, bool hot)
    {
        if (!_boxes.TryGetValue(DiagramLayout.KeyOf(key.Schema, key.Table), out var child)
            || !_boxes.TryGetValue(DiagramLayout.KeyOf(key.ReferencedSchema, key.ReferencedTable), out var parent))
        {
            return;
        }

        var childRelation = _relations.First(r => DiagramLayout.KeyOf(r.Schema, r.Name) == DiagramLayout.KeyOf(key.Schema, key.Table));
        var parentRelation = _relations.First(r => DiagramLayout.KeyOf(r.Schema, r.Name) == DiagramLayout.KeyOf(key.ReferencedSchema, key.ReferencedTable));
        var childY = RowY(childRelation, child, key.Columns.FirstOrDefault());
        var parentY = RowY(parentRelation, parent, key.ReferencedColumns.FirstOrDefault());
        var pen = hot ? HotEdgePen : EdgePen;

        if (ReferenceEquals(childRelation, parentRelation) || child == parent)
        {
            // Self reference: a loop on the right edge.
            var loop = new StreamGeometry();
            using (var g = loop.Open())
            {
                g.BeginFigure(new Point(child.Right, childY), false);
                g.CubicBezierTo(new Point(child.Right + 50, childY), new Point(child.Right + 50, parentY), new Point(child.Right, parentY));
                g.EndFigure(false);
            }

            context.DrawGeometry(null, pen, loop);
            return;
        }

        var parentOnRight = parent.Center.X > child.Center.X;
        var start = new Point(parentOnRight ? child.Right : child.Left, childY);
        var end = new Point(parentOnRight ? parent.Left : parent.Right, parentY);
        var bend = Math.Max(40, Math.Abs(end.X - start.X) / 2);
        var direction = parentOnRight ? 1 : -1;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.CubicBezierTo(new Point(start.X + (bend * direction), start.Y), new Point(end.X - (bend * direction), end.Y), end);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);

        // Crow's foot at the referencing ("many") end, a bar at the referenced ("one") end.
        var foot = start.X + (12 * direction);
        context.DrawLine(pen, new Point(foot, start.Y), new Point(start.X, start.Y - 6));
        context.DrawLine(pen, new Point(foot, start.Y), new Point(start.X, start.Y + 6));
        var bar = end.X - (8 * direction);
        context.DrawLine(pen, new Point(bar, end.Y - 6), new Point(bar, end.Y + 6));
    }

    private double RowY(CatalogRelation relation, Rect box, string? column)
    {
        var columns = Shown(relation).Take(MaxRows).ToList();
        var index = column is null ? -1 : columns.FindIndex(c => c.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? box.Y + (HeaderHeight / 2) : box.Y + HeaderHeight + 4 + (index * RowHeight) + (RowHeight / 2);
    }

    private static FormattedText Text(string text, Typeface typeface, double size, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);
}
