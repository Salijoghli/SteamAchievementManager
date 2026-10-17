namespace SAM.App.Controls.Charts;

/// <summary>One value in a chart.</summary>
/// <param name="AxisLabel">Text under the mark on the x axis, or null to leave it unlabelled (label selectively).</param>
/// <param name="Value">The plotted value (zero or more).</param>
/// <param name="Description">Full text for the tooltip, screen readers and the table view, e.g. "March 2026: 12 unlocks".</param>
/// <param name="Label">Short category text for the table view, e.g. "March 2026".</param>
/// <param name="ValueText">Formatted value for the table view and the direct label.</param>
public sealed record ChartPoint(string? AxisLabel, double Value, string Description, string Label, string ValueText);
