namespace Bengine.Studio.Controls;

using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Snippets;
using Bengine.Enums;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.Sql;

/// <summary>A range of the editor text flagged by an inspection or a server error.</summary>
/// <param name="Start">The start offset.</param>
/// <param name="Length">The length.</param>
/// <param name="Message">The message shown on hover.</param>
/// <param name="IsError">Whether it is an error (red) rather than a warning (amber).</param>
public sealed record EditorMarker(int Start, int Length, string Message, bool IsError = true);

/// <summary>
/// The Studio SQL editor: an AvaloniaEdit text editor with SQL highlighting,
/// catalog-aware completion (pops up as you type and on Ctrl+Space), live
/// templates expanded with Tab (<c>sel</c>, <c>selw</c>, <c>ins</c>, <c>upd</c>,
/// …), squiggly markers for errors and inspections, a subtle highlight of the
/// statement under the caret, find/replace (Ctrl+F / Ctrl+H), reformatting
/// (Ctrl+Alt+L), line comments (Ctrl+/), line duplication (Ctrl+D), and case
/// toggling (Ctrl+Shift+U).
/// </summary>
public sealed class SqlEditor : TextEditor
{
    private static IHighlightingDefinition? _highlighting;
    private readonly MarkerRenderer _markers;
    private readonly StatementRenderer _statement;
    private readonly Avalonia.Threading.DispatcherTimer _inspectTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private CompletionWindow? _completion;
    private IReadOnlyList<EditorMarker> _serverMarkers = [];
    private IReadOnlyList<EditorMarker> _inspectionMarkers = [];

    /// <summary>Creates the editor.</summary>
    public SqlEditor()
    {
        SyntaxHighlighting = Highlighting;
        ShowLineNumbers = true;
        FontFamily = new FontFamily("Cascadia Code, JetBrains Mono, DejaVu Sans Mono, Consolas, Menlo, monospace");
        FontSize = 13;
        Background = new SolidColorBrush(Color.Parse("#0b1015"));
        Foreground = new SolidColorBrush(Color.Parse("#d6deeb"));
        LineNumbersForeground = new SolidColorBrush(Color.Parse("#3d4a5c"));
        Padding = new Thickness(6, 6, 0, 0);
        Options.ConvertTabsToSpaces = true;
        Options.IndentationSize = 4;
        Options.HighlightCurrentLine = true;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.AllowScrollBelowDocument = true;
        TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.Parse("#111a24"));
        TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);
        TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#5a2f6fd6"));
        TextArea.SelectionForeground = null;

        _markers = new MarkerRenderer(this);
        _statement = new StatementRenderer(this);
        TextArea.TextView.BackgroundRenderers.Add(_statement);
        TextArea.TextView.BackgroundRenderers.Add(_markers);
        TextArea.TextEntered += OnTextEntered;
        TextArea.Caret.PositionChanged += (_, _) => _statement.Invalidate();
        TextArea.TextView.PointerMoved += OnPointerMoved;
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _inspectTimer.Tick += (_, _) =>
        {
            _inspectTimer.Stop();
            RunInspections();
        };
        TextChanged += (_, _) =>
        {
            // A replaced document (history, file load) leaves an open list stranded.
            if (_completion is { } open && (CaretOffset < open.StartOffset || open.StartOffset > Document.TextLength))
            {
                open.Close();
            }

            _serverMarkers = [];
            _inspectTimer.Stop();
            if (Inspections && !IsReadOnly)
            {
                _inspectTimer.Start();
            }
            else if (_markers.Markers.Count > 0)
            {
                Refresh(clearInspections: true);
            }

            _statement.Invalidate();
        };
    }

    /// <summary>Gets or sets whether SQL inspections run while typing.</summary>
    public bool Inspections { get; set; } = true;

    /// <summary>Gets or sets whether the connection is read-only (modifying statements are flagged).</summary>
    public bool ReadOnlyConnection { get; set; }

    /// <summary>Gets the current inspection findings (for tests and the status line).</summary>
    public IReadOnlyList<EditorMarker> InspectionMarkers => _inspectionMarkers;

    /// <summary>Runs the inspections now.</summary>
    public void RunInspections()
    {
        if (!Inspections || Document is null)
        {
            return;
        }

        var text = Text;
        if (text.Length > 300_000)
        {
            return;
        }

        var catalog = CatalogSource?.Invoke() ?? DatabaseCatalog.Empty(Provider);
        _inspectionMarkers = SqlInspector.Inspect(text, catalog, ReadOnlyConnection)
            .Select(f => new EditorMarker(f.Start, f.Length, f.Message, f.Severity == InspectionSeverity.Error))
            .ToList();
        Refresh(clearInspections: false);
    }

    private void Refresh(bool clearInspections)
    {
        if (clearInspections)
        {
            _inspectionMarkers = [];
        }

        _markers.Markers = [.. _serverMarkers, .. _inspectionMarkers];
        TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>Gets or sets the SQL dialect (statement splitting, completion, formatting).</summary>
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.PostgreSql;

    /// <summary>Gets or sets the source of the catalog completion draws from.</summary>
    public Func<DatabaseCatalog?>? CatalogSource { get; set; }

    /// <summary>Gets or sets the ranges a server error flagged; cleared when the text changes. Inspections add their own.</summary>
    public IReadOnlyList<EditorMarker> Markers
    {
        get => _markers.Markers;
        set
        {
            _serverMarkers = value;
            Refresh(clearInspections: false);
        }
    }

    /// <summary>Gets the labels of the open completion list, or an empty list when it is closed.</summary>
    public IReadOnlyList<string> CompletionLabels =>
        _completion?.CompletionList.CompletionData.Select(d => d.Text).ToList() ?? [];

    /// <summary>Gets or sets a value indicating whether to shade the statement under the caret.</summary>
    public bool HighlightStatement { get; set; } = true;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextEditor);

    private static IHighlightingDefinition Highlighting
    {
        get
        {
            if (_highlighting is null)
            {
                using var stream = AssetLoader.Open(new Uri("avares://bengine-studio/Assets/SQL.xshd"));
                using var reader = XmlReader.Create(stream);
                _highlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }

            return _highlighting;
        }
    }

    /// <summary>Gets the statement under the caret, or the selection when there is one.</summary>
    /// <returns>The SQL to run with its start offset, or <c>null</c> when the editor is empty.</returns>
    public (string Sql, int Start)? CurrentStatement()
    {
        if (SelectionLength > 0)
        {
            return (SelectedText, SelectionStart);
        }

        return SqlScript.StatementAt(Text, CaretOffset, Provider) is { } statement ? (statement.Text, statement.Start) : null;
    }

    /// <summary>Opens the completion list at the caret.</summary>
    /// <param name="automatic">Whether the list opens while typing (stays closed when there is nothing to offer).</param>
    public void ShowCompletion(bool automatic = false)
    {
        var catalog = CatalogSource?.Invoke() ?? DatabaseCatalog.Empty(Provider);
        var result = SqlCompletion.Complete(Text, CaretOffset, catalog);
        if (result.Items.Count == 0 || (automatic && result.Items.All(i => i.Kind == CompletionKind.Keyword) && result.Prefix.Length < 2))
        {
            return;
        }

        _completion?.Close();
        _completion = new CompletionWindow(TextArea)
        {
            StartOffset = result.ReplaceStart,
            EndOffset = CaretOffset,
            CloseWhenCaretAtBeginning = automatic,
            MinWidth = 380,
        };
        _completion.CompletionList.IsFiltering = true;
        foreach (var item in result.Items)
        {
            _completion.CompletionList.CompletionData.Add(new SqlCompletionData(item));
        }

        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
        if (result.Prefix.Length > 0)
        {
            _completion.CompletionList.SelectItem(result.Prefix);
        }
    }

    /// <summary>Reformats the selection, or the whole text when nothing is selected.</summary>
    public void FormatSql()
    {
        if (SelectionLength > 0)
        {
            var start = SelectionStart;
            var formatted = SqlFormatter.Format(SelectedText, Provider);
            Document.Replace(start, SelectionLength, formatted);
            Select(start, formatted.Length);
        }
        else
        {
            var caret = CaretOffset;
            Document.Text = SqlFormatter.Format(Text, Provider);
            CaretOffset = Math.Min(caret, Document.TextLength);
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || _completion is not null || IsReadOnly)
        {
            return;
        }

        var c = e.Text[^1];
        if (c == '.')
        {
            ShowCompletion(automatic: true);
            return;
        }

        // Pop up at the start of a word (the first identifier character typed).
        if ((char.IsLetter(c) || c == '_') && CaretOffset >= 1)
        {
            var before = CaretOffset >= 2 ? Document.GetCharAt(CaretOffset - 2) : ' ';
            if (!char.IsLetterOrDigit(before) && before is not '_' and not '$' and not '\'' and not ':' and not '@')
            {
                ShowCompletion(automatic: true);
            }
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var keys = Keymap.Current;
        if (keys.Matches("editor.complete", e))
        {
            ShowCompletion();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None && _completion is null && SelectionLength == 0 && !IsReadOnly && TryExpandTemplate())
        {
            e.Handled = true;
        }
        else if (keys.Matches("editor.format", e))
        {
            if (!IsReadOnly)
            {
                FormatSql();
            }

            e.Handled = true;
        }
        else if (keys.Matches("editor.comment", e) && !IsReadOnly)
        {
            ToggleLineComment();
            e.Handled = true;
        }
        else if (keys.Matches("editor.duplicate", e) && !IsReadOnly)
        {
            DuplicateLine();
            e.Handled = true;
        }
        else if (keys.Matches("editor.toggleCase", e) && SelectionLength > 0 && !IsReadOnly)
        {
            ToggleCase();
            e.Handled = true;
        }
        else if (keys.Matches("editor.replace", e))
        {
            OpenReplace();
            e.Handled = true;
        }
    }

    /// <summary>Switches the selection between upper and lower case.</summary>
    public void ToggleCase()
    {
        if (SelectionLength == 0 || IsReadOnly)
        {
            return;
        }

        var text = SelectedText;
        var start = SelectionStart;
        var toggled = text.Any(char.IsLower) ? text.ToUpperInvariant() : text.ToLowerInvariant();
        Document.Replace(start, text.Length, toggled);
        Select(start, toggled.Length);
    }

    /// <summary>Opens the find (and, when editable, replace) panel.</summary>
    public void OpenReplace()
    {
        SearchPanel.Open();
        SearchPanel.IsReplaceMode = !IsReadOnly;
    }

    /// <summary>Comments or uncomments the selected lines.</summary>
    public void ToggleLineComment()
    {
        var first = Document.GetLineByOffset(SelectionLength > 0 ? SelectionStart : CaretOffset);
        var last = Document.GetLineByOffset(SelectionLength > 0 ? SelectionStart + SelectionLength : CaretOffset);
        var lines = new List<DocumentLine>();
        for (var line = first; line is not null; line = line.NextLine)
        {
            lines.Add(line);
            if (line == last)
            {
                break;
            }
        }

        var allCommented = lines.All(l => Document.GetText(l).TrimStart().StartsWith("--", StringComparison.Ordinal) || Document.GetText(l).Trim().Length == 0);
        Document.BeginUpdate();
        foreach (var line in Enumerable.Reverse(lines))
        {
            var text = Document.GetText(line);
            var indent = text.Length - text.TrimStart().Length;
            if (allCommented)
            {
                var at = text.IndexOf("--", StringComparison.Ordinal);
                if (at >= 0)
                {
                    Document.Remove(line.Offset + at, at + 2 < text.Length && text[at + 2] == ' ' ? 3 : 2);
                }
            }
            else if (text.Trim().Length > 0)
            {
                Document.Insert(line.Offset + indent, "-- ");
            }
        }

        Document.EndUpdate();
    }

    /// <summary>Duplicates the current line or selection.</summary>
    public void DuplicateLine()
    {
        if (SelectionLength > 0)
        {
            var text = SelectedText;
            Document.Insert(SelectionStart + SelectionLength, text);
            return;
        }

        var line = Document.GetLineByOffset(CaretOffset);
        var column = CaretOffset - line.Offset;
        Document.Insert(line.EndOffset, "\n" + Document.GetText(line));
        CaretOffset = line.NextLine!.Offset + column;
    }

    // DataGrip-style abbreviations; ${x} marks a field, ${END} the final caret.
    private static readonly Dictionary<string, string> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sel"] = "SELECT * FROM ${table}${END};",
        ["selw"] = "SELECT * FROM ${table} ${alias} WHERE ${alias}.${END};",
        ["selc"] = "SELECT COUNT(*) FROM ${table} ${alias} WHERE ${alias}.${END};",
        ["lim"] = "SELECT * FROM ${table} LIMIT ${100}${END};",
        ["top"] = "SELECT TOP ${100} * FROM ${table}${END};",
        ["ins"] = "INSERT INTO ${table} (${columns}) VALUES (${values}${END});",
        ["upd"] = "UPDATE ${table} SET ${column} = ${value} WHERE ${END};",
        ["del"] = "DELETE FROM ${table} WHERE ${END};",
        ["tab"] = "CREATE TABLE ${table} (\n    ${id} ${bigint} ${NOT NULL}${END}\n);",
        ["col"] = "${column} ${type} ${NULL}${END}",
        ["view"] = "CREATE VIEW ${view} AS SELECT * FROM ${table}${END};",
        ["cnt"] = "SELECT COUNT(*) FROM ${table}${END};",
    };

    private bool TryExpandTemplate()
    {
        var caret = CaretOffset;
        var start = caret;
        while (start > 0 && char.IsLetter(Document.GetCharAt(start - 1)))
        {
            start--;
        }

        var word = Document.GetText(start, caret - start);
        if (word.Length == 0 || !Templates.TryGetValue(word, out var template) || (start > 0 && Document.GetCharAt(start - 1) is '.' or '_'))
        {
            return false;
        }

        if (Provider == DatabaseProvider.SqlServer && word.Equals("lim", StringComparison.OrdinalIgnoreCase))
        {
            template = Templates["top"];
        }

        var snippet = new Snippet();
        var fields = new Dictionary<string, SnippetReplaceableTextElement>(StringComparer.Ordinal);
        var position = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(template, @"\$\{([^}]*)\}"))
        {
            if (match.Index > position)
            {
                snippet.Elements.Add(new SnippetTextElement { Text = template[position..match.Index] });
            }

            var name = match.Groups[1].Value;
            if (name == "END")
            {
                snippet.Elements.Add(new SnippetCaretElement());
            }
            else if (fields.TryGetValue(name, out var existing))
            {
                snippet.Elements.Add(new SnippetBoundElement { TargetElement = existing });
            }
            else
            {
                var field = new SnippetReplaceableTextElement { Text = name };
                fields[name] = field;
                snippet.Elements.Add(field);
            }

            position = match.Index + match.Length;
        }

        if (position < template.Length)
        {
            snippet.Elements.Add(new SnippetTextElement { Text = template[position..] });
        }

        Document.Remove(start, caret - start);
        CaretOffset = start;
        snippet.Insert(TextArea);
        return true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var view = TextArea.TextView;
        var position = view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset);
        var marker = position is { } p && Document is not null
            ? _markers.Markers.FirstOrDefault(m =>
            {
                var offset = Document.GetOffset(p.Location);
                return offset >= m.Start && offset <= m.Start + Math.Max(1, m.Length);
            })
            : null;

        ToolTip.SetTip(TextArea, marker?.Message);
        ToolTip.SetIsOpen(TextArea, marker is not null);
    }

    /// <summary>Draws wavy underlines under the markers.</summary>
    private sealed class MarkerRenderer(SqlEditor editor) : IBackgroundRenderer
    {
        private static readonly IPen ErrorPen = new Pen(new SolidColorBrush(Color.Parse("#ff6b6b")), 1.2);
        private static readonly IPen WarningPen = new Pen(new SolidColorBrush(Color.Parse("#e0b341")), 1.2);

        public IReadOnlyList<EditorMarker> Markers { get; set; } = [];

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Markers.Count == 0 || !textView.VisualLinesValid || editor.Document is null)
            {
                return;
            }

            var length = editor.Document.TextLength;
            foreach (var marker in Markers)
            {
                var start = Math.Clamp(marker.Start, 0, length);
                var end = Math.Clamp(marker.Start + Math.Max(1, marker.Length), 0, length);
                if (end <= start)
                {
                    start = Math.Max(0, end - 1);
                }

                var segment = new TextSegment { StartOffset = start, EndOffset = end };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        var y = rect.Bottom - 1;
                        context.BeginFigure(new Point(rect.Left, y), false);
                        var up = true;
                        for (var x = rect.Left; x < rect.Right; x += 2.5)
                        {
                            context.LineTo(new Point(Math.Min(x + 2.5, rect.Right), up ? y - 2 : y));
                            up = !up;
                        }

                        context.EndFigure(false);
                    }

                    drawingContext.DrawGeometry(null, marker.IsError ? ErrorPen : WarningPen, geometry);
                }
            }
        }
    }

    /// <summary>Shades the statement under the caret when the editor holds several.</summary>
    private sealed class StatementRenderer(SqlEditor editor) : IBackgroundRenderer
    {
        private static readonly IBrush Shade = new SolidColorBrush(Color.Parse("#0f1a26"));
        private static readonly IPen Bar = new Pen(new SolidColorBrush(Color.Parse("#2f6fd6")), 2);
        private (int Start, int End)? _range;
        private bool _dirty = true;

        public KnownLayer Layer => KnownLayer.Background;

        public void Invalidate()
        {
            _dirty = true;
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!editor.HighlightStatement || editor.Document is null || !textView.VisualLinesValid || editor.SelectionLength > 0)
            {
                return;
            }

            if (_dirty)
            {
                _dirty = false;
                var text = editor.Text;
                var statements = text.Length > 200_000 ? [] : SqlScript.Split(text, editor.Provider);
                var current = statements.Count > 1 ? SqlScript.StatementAt(text, editor.CaretOffset, editor.Provider) : null;
                _range = current is null ? null : (current.Start, current.End);
            }

            if (_range is not { } range)
            {
                return;
            }

            var first = editor.Document.GetLineByOffset(Math.Min(range.Start, editor.Document.TextLength));
            var last = editor.Document.GetLineByOffset(Math.Min(range.End, editor.Document.TextLength));
            var segment = new TextSegment { StartOffset = first.Offset, EndOffset = last.EndOffset };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment, extendToFullWidthAtLineEnd: true))
            {
                var full = new Rect(0, rect.Top, textView.Bounds.Width, rect.Height);
                drawingContext.FillRectangle(Shade, full);
                drawingContext.DrawLine(Bar, new Point(1, rect.Top), new Point(1, rect.Bottom));
            }
        }
    }
}

/// <summary>A completion proposal shown in the editor's list.</summary>
internal sealed class SqlCompletionData(CompletionItem item) : ICompletionData
{
    private static readonly Dictionary<CompletionKind, (string Glyph, string Color)> Glyphs = new()
    {
        [CompletionKind.Keyword] = ("k", "#c792ea"),
        [CompletionKind.Schema] = ("s", "#8b98ab"),
        [CompletionKind.Table] = ("t", "#e0862f"),
        [CompletionKind.View] = ("v", "#3ccf7e"),
        [CompletionKind.Column] = ("c", "#82aaff"),
        [CompletionKind.Function] = ("f", "#89ddff"),
        [CompletionKind.Routine] = ("f", "#89ddff"),
        [CompletionKind.Join] = ("⋈", "#ffcb6b"),
        [CompletionKind.Condition] = ("=", "#ffcb6b"),
        [CompletionKind.Alias] = ("a", "#f7b98c"),
    };

    public IImage? Image => null;

    public string Text => item.Label;

    public object Content
    {
        get
        {
            var (glyph, color) = Glyphs[item.Kind];
            return new DockPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = item.Detail ?? string.Empty,
                        Foreground = new SolidColorBrush(Color.Parse("#5d6a7d")),
                        Margin = new Thickness(18, 0, 4, 0),
                        [DockPanel.DockProperty] = Dock.Right,
                    },
                    new TextBlock
                    {
                        Text = glyph,
                        Width = 16,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse(color)),
                        [DockPanel.DockProperty] = Dock.Left,
                    },
                    new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center },
                },
            };
        }
    }

    public object Description => item.Kind switch
    {
        CompletionKind.Join => $"Join on foreign key {item.Detail}",
        CompletionKind.Condition => $"Condition from foreign key {item.Detail}",
        _ => item.Detail ?? item.Kind.ToString(),
    };

    public double Priority => item.Score;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var text = item.InsertText;
        if (item.Kind == CompletionKind.Keyword && completionSegment.EndOffset >= textArea.Document.TextLength
            || item.Kind == CompletionKind.Keyword && !char.IsWhiteSpace(textArea.Document.GetCharAt(completionSegment.EndOffset)))
        {
            text += " ";
        }

        textArea.Document.Replace(completionSegment, text);
    }
}
