namespace Bengine.Studio.ViewModels;

using System.Globalization;
using Bengine.Introspection.Plans;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>One row of the plan grid: an operation at its depth.</summary>
/// <param name="Node">The operation.</param>
/// <param name="Depth">Its depth in the tree.</param>
/// <param name="Share">Its share of the plan's time (analyzed) or cost, 0–1.</param>
public sealed record PlanRow(PlanNode Node, int Depth, double Share)
{
    /// <summary>Gets the indented operation label.</summary>
    public string Operation => new string(' ', Depth * 3) + (Depth > 0 ? "└ " : string.Empty) + Node.Operation;

    /// <summary>Gets the object.</summary>
    public string? Object => Node.Object;

    /// <summary>Gets the cost.</summary>
    public string Cost => Node.TotalCost is { } c ? c.ToString("#,0.##", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Gets the estimated rows.</summary>
    public string Estimated => Node.EstimatedRows is { } r ? r.ToString("#,0.##", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Gets the actual rows.</summary>
    public string Actual => Node.ActualRows is { } r ? r.ToString("#,0.##", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Gets the loops.</summary>
    public string Loops => Node.Loops is { } l && l != 1 ? l.ToString("#,0", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Gets the inclusive time.</summary>
    public string Time => Node.ActualTimeMs is { } t ? (t * (Node.Loops ?? 1)).ToString("#,0.###", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Gets the share as a percentage label.</summary>
    public string Percent => Share > 0 ? $"{Share * 100:0.#}%" : string.Empty;

    /// <summary>Gets the bar width for the share (0–120 px).</summary>
    public double Bar => Math.Round(Share * 120, 1);

    /// <summary>Gets whether the operation has warnings.</summary>
    public bool HasWarning => Node.Warnings.Count > 0;

    /// <summary>Gets the warnings and details for the side panel.</summary>
    public string Notes => string.Join("\n", Node.Warnings.Select(w => "⚠ " + w).Concat(Node.Details.Select(d => $"{d.Key}: {d.Value}")));
}

/// <summary>An execution plan result tab of the Query Tool.</summary>
public sealed partial class PlanResultViewModel : ObservableObject
{
    [ObservableProperty] private PlanRow? _selected;
    [ObservableProperty] private bool _showRaw;

    /// <summary>Creates the tab.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="sql">The explained statement.</param>
    public PlanResultViewModel(QueryPlan plan, string sql)
    {
        Plan = plan;
        Sql = sql;
        var nodes = plan.Flatten().ToList();
        var total = plan.Analyzed
            ? nodes.Sum(n => n.Node.SelfTimeMs ?? 0)
            : plan.Roots.Sum(r => r.TotalCost ?? 0);
        Rows = nodes.Select(n => new PlanRow(n.Node, n.Depth, total <= 0 ? 0
            : plan.Analyzed ? (n.Node.SelfTimeMs ?? 0) / total
            : Math.Max(0, (n.Node.TotalCost ?? 0) - n.Node.Children.Sum(c => c.TotalCost ?? 0)) / total)).ToList();
        Selected = Rows.OrderByDescending(r => r.HasWarning).ThenByDescending(r => r.Share).FirstOrDefault();
    }

    /// <summary>Gets the tab title.</summary>
    public string Title => Plan.Analyzed ? "Plan (analyzed)" : "Plan";

    /// <summary>Gets the plan.</summary>
    public QueryPlan Plan { get; }

    /// <summary>Gets the explained statement.</summary>
    public string Sql { get; }

    /// <summary>Gets the rows.</summary>
    public IReadOnlyList<PlanRow> Rows { get; }

    /// <summary>Gets the summary line.</summary>
    public string Summary =>
        $"{Rows.Count} operation(s)" +
        (Plan.PlanningTimeMs is { } p ? $" · planning {p:0.###} ms" : string.Empty) +
        (Plan.ExecutionTimeMs is { } e ? $" · execution {e:0.###} ms" : string.Empty) +
        (Rows.Count(r => r.HasWarning) is var w and > 0 ? $" · {w} warning(s)" : string.Empty) +
        (Plan.Analyzed ? " · bars show time spent in each operation" : " · bars show each operation's own cost");
}
