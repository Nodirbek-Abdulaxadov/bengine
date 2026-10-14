namespace Bengine.Studio.Views;

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Bengine.Enums;
using Bengine.Introspection.History;
using Bengine.Introspection.Projects;
using Bengine.Introspection.Sql;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;

/// <summary>
/// The base of the Studio's code-built dialogs: the panel background, a body,
/// and a footer of buttons on the right. Escape cancels.
/// </summary>
public abstract class StudioDialog<T> : Window
{
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };

    /// <summary>Creates the dialog.</summary>
    /// <param name="title">The title.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height, or <c>null</c> to size to content.</param>
    protected StudioDialog(string title, double width, double? height = null)
    {
        Title = title;
        Width = width;
        if (height is { } h)
        {
            Height = h;
        }
        else
        {
            SizeToContent = SizeToContent.Height;
        }

        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (IBrush?)Application.Current?.FindResource("Panel");
        FontSize = 13;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(default(T));
            }
        };
    }

    /// <summary>Sets the dialog body.</summary>
    /// <param name="body">The body.</param>
    protected void SetBody(Control body)
    {
        var footer = new Border
        {
            BorderBrush = (IBrush?)Application.Current?.FindResource("Line"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 10),
            Child = _buttons,
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel { Children = { footer, new Border { Padding = new Thickness(16, 14), Child = body } } };
    }

    /// <summary>Adds a footer button.</summary>
    /// <param name="text">The caption.</param>
    /// <param name="onClick">The action.</param>
    /// <param name="primary">Whether it is the default (Enter) button.</param>
    /// <returns>The button.</returns>
    protected Button AddButton(string text, Action onClick, bool primary = false)
    {
        var button = new Button { Content = text, Classes = { primary ? "primary" : "ghost" }, IsDefault = primary };
        button.Click += (_, _) => onClick();
        _buttons.Children.Add(button);
        return button;
    }
}

/// <summary>Asks for the values of a statement's parameters.</summary>
public sealed class ParametersDialog : StudioDialog<IReadOnlyDictionary<string, string>?>
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="names">The parameter names.</param>
    /// <param name="previous">Values entered last time.</param>
    public ParametersDialog(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> previous)
        : base("Parameters", 520)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var boxes = new Dictionary<string, TextBox>();
        for (var i = 0; i < names.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = names[i].StartsWith('?') ? $"#{names[i][1..]}" : ":" + names[i], VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)Application.Current!.FindResource("Mono")!, Margin = new Thickness(0, 4, 12, 4) };
            var box = new TextBox { Text = previous.GetValueOrDefault(names[i], string.Empty), Watermark = "value as SQL: 42, 'text', now()", FontFamily = label.FontFamily, Margin = new Thickness(0, 4) };
            Grid.SetRow(label, i);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
            boxes[names[i]] = box;
        }

        SetBody(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Values are inserted into the SQL as typed — quote text values.", Classes = { "dim" }, TextWrapping = TextWrapping.Wrap },
                grid,
            },
        });
        AddButton("Cancel", () => Close(null));
        AddButton("Run", () => Close(boxes.ToDictionary(b => b.Key, b => b.Value.Text ?? string.Empty)), primary: true);
        Opened += (_, _) => boxes.Values.FirstOrDefault()?.Focus();
    }
}

/// <summary>Shows SQL (pending changes, generated DDL) with an optional confirm button.</summary>
public sealed class ReviewSqlDialog : StudioDialog<bool>
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="title">The title.</param>
    /// <param name="sql">The SQL.</param>
    /// <param name="confirmText">The confirm caption, or <c>null</c> for view-only.</param>
    /// <param name="provider">The dialect for highlighting.</param>
    public ReviewSqlDialog(string title, string sql, string? confirmText, DatabaseProvider provider)
        : base(title, 900, 560)
    {
        var editor = new SqlEditor { Text = sql, IsReadOnly = true, Provider = provider, HighlightStatement = false };
        SetBody(new Border { BorderBrush = (IBrush?)Application.Current?.FindResource("Line"), BorderThickness = new Thickness(1), Child = editor });
        AddButton("Copy", () => _ = Clipboard?.SetTextAsync(sql));
        if (confirmText is null)
        {
            AddButton("Close", () => Close(false), primary: true);
        }
        else
        {
            AddButton("Cancel", () => Close(false));
            AddButton(confirmText, () => Close(true), primary: true);
        }
    }
}

/// <summary>Views or edits one value in a large editor, with JSON formatting and a NULL toggle.</summary>
public sealed class ValueDialog : StudioDialog<string?>
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="title">The title.</param>
    /// <param name="value">The value text, or <see cref="RowItem.NullText"/>.</param>
    /// <param name="readOnly">Whether the value can be changed.</param>
    public ValueDialog(string title, string? value, bool readOnly)
        : base(title, 760, 520)
    {
        var isNull = value == RowItem.NullText;
        var box = new TextBox
        {
            Text = isNull ? string.Empty : value,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = readOnly,
            FontFamily = (FontFamily)Application.Current!.FindResource("Mono")!,
            FontSize = 13,
            [ScrollViewer.VerticalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        var nullBox = new CheckBox { Content = "NULL", IsChecked = isNull, IsEnabled = !readOnly };
        nullBox.IsCheckedChanged += (_, _) => box.IsEnabled = nullBox.IsChecked != true;
        box.IsEnabled = !isNull;
        var info = new TextBlock { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center };
        void Count() => info.Text = $"{box.Text?.Length ?? 0:N0} characters";
        box.TextChanged += (_, _) => Count();
        Count();
        var wrap = new CheckBox { Content = "Wrap", IsChecked = true };
        wrap.IsCheckedChanged += (_, _) => box.TextWrapping = wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 8) };
        var format = new Button { Content = "Format JSON", Classes = { "ghost" } };
        format.Click += (_, _) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(box.Text ?? string.Empty);
                var pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                if (readOnly)
                {
                    box.IsReadOnly = false;
                    box.Text = pretty;
                    box.IsReadOnly = true;
                }
                else
                {
                    box.Text = pretty;
                }
            }
            catch (JsonException ex)
            {
                info.Text = "Not JSON: " + ex.Message;
            }
        };
        tools.Children.Add(format);
        tools.Children.Add(wrap);
        tools.Children.Add(nullBox);
        tools.Children.Add(info);
        DockPanel.SetDock(tools, Dock.Top);
        SetBody(new DockPanel { Children = { tools, box } });

        AddButton("Copy", () => _ = Clipboard?.SetTextAsync(box.Text ?? string.Empty));
        if (readOnly)
        {
            AddButton("Close", () => Close(null), primary: true);
        }
        else
        {
            AddButton("Cancel", () => Close(null));
            AddButton("Apply", () => Close(nullBox.IsChecked == true ? RowItem.NullText : box.Text ?? string.Empty), primary: true);
        }

        Opened += (_, _) => box.Focus();
    }
}

/// <summary>Asks for one line of text.</summary>
public sealed class PromptDialog : StudioDialog<string?>
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="title">The title.</param>
    /// <param name="label">The prompt.</param>
    /// <param name="initial">The initial text.</param>
    public PromptDialog(string title, string label, string initial)
        : base(title, 460)
    {
        var box = new TextBox { Text = initial };
        SetBody(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, box } });
        AddButton("Cancel", () => Close(null));
        AddButton("OK", () => Close(box.Text), primary: true);
        Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
    }
}

/// <summary>Browses the query history: search, preview, and reuse a statement.</summary>
public sealed class HistoryDialog : StudioDialog<QueryHistoryEntry?>
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="history">The history.</param>
    /// <param name="serverId">The server to show, or <c>null</c> for all.</param>
    public HistoryDialog(QueryHistory history, string? serverId)
        : base("Query History", 980, 620)
    {
        var search = new TextBox { Watermark = "Search statements…" };
        var allServers = new CheckBox { Content = "All servers", IsChecked = serverId is null };
        var list = new ListBox();
        var preview = new SqlEditor { IsReadOnly = true, HighlightStatement = false, ShowLineNumbers = false };
        void Load() => list.ItemsSource = history.List(allServers.IsChecked == true ? null : serverId, search.Text).ToList();
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<QueryHistoryEntry>((entry, _) => entry is null ? new TextBlock() : new DockPanel
        {
            Children =
            {
                new TextBlock
                {
                    [DockPanel.DockProperty] = Dock.Right,
                    Classes = { "faint" },
                    FontSize = 11.5,
                    Margin = new Thickness(12, 0, 0, 0),
                    Text = $"{entry.Database}  ·  {entry.DurationMs:N0} ms" + (entry.Rows is { } r ? $"  ·  {r:N0} rows" : string.Empty),
                },
                new TextBlock
                {
                    [DockPanel.DockProperty] = Dock.Left,
                    Classes = { "faint", "mono" },
                    FontSize = 11.5,
                    Width = 120,
                    Text = entry.Time.ToLocalTime().ToString("MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                },
                new TextBlock
                {
                    Classes = { "mono" },
                    FontSize = 12.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = entry.Error is null ? (IBrush?)Application.Current?.FindResource("Dim") : new SolidColorBrush(Color.Parse("#ff8f8f")),
                    Text = string.Join(' ', entry.Sql.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())),
                },
            },
        });
        list.SelectionChanged += (_, _) => preview.Text = (list.SelectedItem as QueryHistoryEntry)?.Sql ?? string.Empty;
        list.DoubleTapped += (_, _) => Close(list.SelectedItem as QueryHistoryEntry);
        search.TextChanged += (_, _) => Load();
        allServers.IsCheckedChanged += (_, _) => Load();
        Load();

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = { new Border { [DockPanel.DockProperty] = Dock.Right, Margin = new Thickness(10, 0, 0, 0), Child = allServers }, search } };
        DockPanel.SetDock(top, Dock.Top);
        var split = new Grid { RowDefinitions = new RowDefinitions("3*,4,2*") };
        Grid.SetRow(list, 0);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = (IBrush?)Application.Current?.FindResource("Line") };
        Grid.SetRow(splitter, 1);
        var previewBorder = new Border { Child = preview, BorderBrush = (IBrush?)Application.Current?.FindResource("Line"), BorderThickness = new Thickness(1) };
        Grid.SetRow(previewBorder, 2);
        split.Children.Add(list);
        split.Children.Add(splitter);
        split.Children.Add(previewBorder);
        SetBody(new DockPanel { Children = { top, split } });
        AddButton("Close", () => Close(null));
        AddButton("Insert into Editor", () => Close(list.SelectedItem as QueryHistoryEntry), primary: true);
        Opened += (_, _) => search.Focus();
    }
}

/// <summary>"Go to object" (Ctrl+N): finds tables and views across every opened database by name.</summary>
public sealed class GoToDialog : StudioDialog<ObjectHit?>
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="explorer">The explorer whose metadata cache is searched.</param>
    public GoToDialog(ExplorerViewModel explorer)
        : base("Go to Object", 640, 480)
    {
        var search = new TextBox { Watermark = "Table or view name (initials work: oi → order_items)" };
        var status = new TextBlock { Classes = { "dim" }, Margin = new Thickness(0, 6, 0, 0) };
        var list = new ListBox
        {
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ObjectHit>((hit, _) => hit is null ? new TextBlock() : new DockPanel
            {
                Children =
                {
                    new TextBlock { [DockPanel.DockProperty] = Dock.Right, Classes = { "faint" }, FontSize = 11.5, Text = hit.Location },
                    new IconView { [DockPanel.DockProperty] = Dock.Left, Kind = hit.Kind, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = hit.Label },
                },
            }),
        };

        async Task SearchAsync()
        {
            var hits = await explorer.SearchObjectsAsync(search.Text ?? string.Empty);
            list.ItemsSource = hits;
            if (hits.Count > 0)
            {
                list.SelectedIndex = 0;
            }

            status.Text = hits.Count == 0
                ? "No match in the loaded databases. Expand a server to include its databases."
                : $"{hits.Count} match(es)";
        }

        search.TextChanged += (_, _) => _ = SearchAsync();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list.ItemCount > 0)
            {
                list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && list.ItemCount > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Close(list.SelectedItem as ObjectHit);
                e.Handled = true;
            }
        };
        list.DoubleTapped += (_, _) => Close(list.SelectedItem as ObjectHit);

        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(status, Dock.Bottom);
        SetBody(new DockPanel { Children = { search, status, new Border { Margin = new Thickness(0, 8, 0, 0), Child = list } } });
        AddButton("Cancel", () => Close(null));
        AddButton("Open", () => Close(list.SelectedItem as ObjectHit), primary: true);
        Opened += async (_, _) =>
        {
            search.Focus();
            status.Text = "Loading metadata of connected servers…";
            await explorer.WarmCatalogsAsync();
            await SearchAsync();
        };
    }
}

/// <summary>A file's local history: every saved (and run) version, its diff against the editor, and Restore.</summary>
public sealed class LocalHistoryDialog : StudioDialog<string?>
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="title">The title.</param>
    /// <param name="versions">The versions, newest first.</param>
    /// <param name="current">The current text.</param>
    /// <param name="provider">The dialect for highlighting.</param>
    public LocalHistoryDialog(string title, IReadOnlyList<HistoryVersion> versions, string current, DatabaseProvider provider)
        : base(title, 1040, 640)
    {
        var list = new ListBox { ItemsSource = versions, Width = 250 };
        var preview = new SqlEditor { IsReadOnly = true, HighlightStatement = false, Provider = provider };
        var showDiff = new CheckBox { Content = "Show changes against the editor", IsChecked = true };
        var summary = new TextBlock { Classes = { "dim" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<HistoryVersion>((version, _) => version is null ? new TextBlock() : new StackPanel
        {
            Children =
            {
                new TextBlock { Text = version.Time.ToString("yyyy-MM-dd  HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), Classes = { "mono" }, FontSize = 12.5 },
                new TextBlock { Text = Ago(version.Time) + $"  ·  {version.Length:N0} chars", Classes = { "faint" }, FontSize = 11.5 },
            },
        });

        string? Selected() => list.SelectedItem is HistoryVersion v && File.Exists(v.Path) ? File.ReadAllText(v.Path) : null;
        void Show()
        {
            var text = Selected();
            if (text is null)
            {
                preview.Text = versions.Count == 0 ? "-- No saved versions yet. Versions are recorded on every save and run." : string.Empty;
                summary.Text = string.Empty;
                return;
            }

            var diff = TextDiff.Lines(text, current);
            var added = diff.Count(l => l.Kind == DiffKind.Added);
            var removed = diff.Count(l => l.Kind == DiffKind.Removed);
            summary.Text = added + removed == 0 ? "Same as the editor." : $"Editor vs this version: +{added} −{removed} line(s)";
            preview.Text = showDiff.IsChecked == true && added + removed > 0 ? TextDiff.Unified(diff) : text;
        }

        list.SelectionChanged += (_, _) => Show();
        showDiff.IsCheckedChanged += (_, _) => Show();
        list.DoubleTapped += (_, _) => Close(Selected());

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Children = { showDiff, summary } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(list, Dock.Left);
        var previewBorder = new Border { Margin = new Thickness(10, 0, 0, 0), Child = preview, BorderBrush = (IBrush?)Application.Current?.FindResource("Line"), BorderThickness = new Thickness(1) };
        SetBody(new DockPanel { Children = { top, list, previewBorder } });
        AddButton("Close", () => Close(null));
        AddButton("Restore", () => Close(Selected()), primary: true);
        if (versions.Count > 0)
        {
            list.SelectedIndex = 0;
        }
        else
        {
            Show();
        }
    }

    private static string Ago(DateTime time)
    {
        var span = DateTime.Now - time;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : $"{(int)span.TotalDays} d ago";
    }
}

/// <summary>"Find Usages": where an object is referenced, in the database and in open SQL.</summary>
public sealed class UsagesDialog : StudioDialog<Usage?>
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="title">The title.</param>
    /// <param name="usages">The usages.</param>
    public UsagesDialog(string title, IReadOnlyList<Usage> usages)
        : base(title, 860, 520)
    {
        var list = new ListBox
        {
            ItemsSource = usages,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Usage>((usage, _) => usage is null ? new TextBlock() : new DockPanel
            {
                Children =
                {
                    new TextBlock { [DockPanel.DockProperty] = Dock.Left, Text = usage.Kind, Classes = { "faint" }, Width = 130, FontSize = 12 },
                    new TextBlock { [DockPanel.DockProperty] = Dock.Left, Text = usage.Name + (usage.Line is { } line ? $":{line}" : string.Empty), Width = 240, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) },
                    new TextBlock { Text = usage.Snippet, Classes = { "mono", "dim" }, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            }),
        };
        var status = new TextBlock
        {
            Classes = { "dim" },
            Margin = new Thickness(0, 0, 0, 8),
            Text = usages.Count == 0 ? "No usages found." : $"{usages.Count} usage(s) in {usages.Select(u => u.Kind).Distinct().Count()} kind(s) of source.",
        };
        DockPanel.SetDock(status, Dock.Top);
        list.DoubleTapped += (_, _) => Close(list.SelectedItem as Usage);
        SetBody(new DockPanel { Children = { status, list } });
        AddButton("Close", () => Close(null));
        AddButton("Open", () => Close(list.SelectedItem as Usage), primary: true);
    }
}

/// <summary>"Find Action" (Ctrl+Shift+A): every keymap action, searchable by name, with its shortcut.</summary>
public sealed class ActionPaletteDialog : StudioDialog<string?>
{
    /// <summary>Creates the dialog.</summary>
    public ActionPaletteDialog()
        : base("Find Action", 620, 480)
    {
        var search = new TextBox { Watermark = "Action name (initials work: rs → Run Script)" };
        var list = new ListBox
        {
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<KeyAction>((action, _) => action is null ? new TextBlock() : new DockPanel
            {
                Children =
                {
                    new TextBlock { [DockPanel.DockProperty] = Dock.Right, Classes = { "faint", "mono" }, FontSize = 11.5, Text = Keymap.Current.Display(action.Id) },
                    new TextBlock { [DockPanel.DockProperty] = Dock.Left, Classes = { "faint" }, Width = 80, FontSize = 11.5, Text = action.Category, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = action.Title },
                },
            }),
        };

        void Filter()
        {
            var text = search.Text ?? string.Empty;
            list.ItemsSource = Keymap.Actions
                .Select(a => (Action: a, Score: text.Length == 0 ? 1 : Math.Max(SqlCompletion.MatchScore(a.Title, text), SqlCompletion.MatchScore(a.Id, text))))
                .Where(p => p.Score > 0)
                .OrderByDescending(p => p.Score)
                .Select(p => p.Action)
                .ToList();
            list.SelectedIndex = list.ItemCount > 0 ? 0 : -1;
        }

        search.TextChanged += (_, _) => Filter();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && list.ItemCount > 0)
            {
                list.SelectedIndex = Math.Min(list.ItemCount - 1, list.SelectedIndex + 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && list.ItemCount > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Close((list.SelectedItem as KeyAction)?.Id);
                e.Handled = true;
            }
        };
        list.DoubleTapped += (_, _) => Close((list.SelectedItem as KeyAction)?.Id);
        Filter();
        DockPanel.SetDock(search, Dock.Top);
        SetBody(new DockPanel { Children = { search, new Border { Margin = new Thickness(0, 8, 0, 0), Child = list } } });
        AddButton("Cancel", () => Close(null));
        AddButton("Run", () => Close((list.SelectedItem as KeyAction)?.Id), primary: true);
        Opened += (_, _) => search.Focus();
    }
}

/// <summary>The Keymap: every action's shortcuts, rebound by pressing keys; returns the overrides to keep.</summary>
public sealed class KeymapDialog : StudioDialog<IReadOnlyDictionary<string, string>?>
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="overrides">The current overrides.</param>
    public KeymapDialog(IReadOnlyDictionary<string, string> overrides)
        : base("Keymap", 760, 620)
    {
        var values = Keymap.Actions.ToDictionary(a => a.Id, a => overrides.TryGetValue(a.Id, out var custom) ? custom : a.Default, StringComparer.Ordinal);
        var warning = new TextBlock { Classes = { "dim" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var rows = new StackPanel { Spacing = 2 };
        void Check()
        {
            var conflicts = values
                .SelectMany(p => Keymap.Parse(p.Value).Select(g => (Gesture: Keymap.Format(g), Category: Keymap.Actions.First(a => a.Id == p.Key).Category, p.Key)))
                .GroupBy(x => (x.Gesture, Scope: x.Category is "Query" or "Editor" ? "editor" : x.Category == "Explorer" ? "tree" : "window"))
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key.Gesture}: {string.Join(", ", g.Select(x => Keymap.Actions.First(a => a.Id == x.Key).Title))}")
                .ToList();
            warning.Text = conflicts.Count == 0
                ? "Click a shortcut and press the keys; Backspace clears it. Several shortcuts are separated by commas."
                : "Conflicts — " + string.Join("; ", conflicts);
            warning.Foreground = conflicts.Count == 0 ? (IBrush?)Application.Current?.FindResource("Dim") : new SolidColorBrush(Color.Parse("#e0b341"));
        }

        foreach (var group in Keymap.Actions.GroupBy(a => a.Category))
        {
            rows.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            foreach (var action in group)
            {
                var box = new TextBox { Text = values[action.Id], Width = 280, FontFamily = (FontFamily?)Application.Current?.FindResource("Mono") ?? FontFamily.Default, MinHeight = 26, Padding = new Thickness(6, 2) };
                box.AddHandler(KeyDownEvent, (_, e) =>
                {
                    if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.Tab)
                    {
                        return;
                    }

                    box.Text = e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None ? string.Empty : Keymap.Format(new KeyGesture(e.Key, e.KeyModifiers));
                    e.Handled = true;
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                box.TextChanged += (_, _) =>
                {
                    values[action.Id] = box.Text ?? string.Empty;
                    Check();
                };
                var reset = new Button { Content = "↺", Classes = { "tool" } };
                ToolTip.SetTip(reset, $"Reset to {(action.Default.Length == 0 ? "none" : action.Default)}");
                reset.Click += (_, _) => box.Text = action.Default;
                rows.Children.Add(new DockPanel
                {
                    Children =
                    {
                        new StackPanel { [DockPanel.DockProperty] = Dock.Right, Orientation = Orientation.Horizontal, Spacing = 4, Children = { box, reset } },
                        new TextBlock { Text = action.Title, VerticalAlignment = VerticalAlignment.Center },
                    },
                });
            }
        }

        Check();
        DockPanel.SetDock(warning, Dock.Bottom);
        SetBody(new DockPanel { Children = { warning, new ScrollViewer { Content = rows } } });
        AddButton("Reset All", () =>
        {
            Close(new Dictionary<string, string>(StringComparer.Ordinal));
        });
        AddButton("Cancel", () => Close(null));
        AddButton("Save", () => Close(values.Where(p => p.Value != Keymap.Actions.First(a => a.Id == p.Key).Default).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)), primary: true);
    }
}
