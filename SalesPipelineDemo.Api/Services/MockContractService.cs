using SalesPipelineDemo.Api.Models;

namespace SalesPipelineDemo.Api.Services;

public class MockContractService
{
    private static readonly string[] StageOptions =
        ["Prospecting", "Qualified", "Demo", "Proposal", "Negotiation", "Closed Won", "Closed Lost"];

    private static readonly string[] OwnerOptions =
        ["Alice Johnson", "Bob Martinez", "Carol White", "David Lee", "John Smith"];

    /// <summary>
    /// Builds a grid contract for the given opportunities, defining columns and row visual styles.
    /// </summary>
    public GridDataContract BuildGridContract(List<Opportunity> opps) => new()
    {
        Title   = "Sales Opportunities",
        Columns =
        [
            new() { FieldName = "customer",    DisplayName = "Customer",        DataType = "text",   Width = 170, IsEditable = false },
            new() { FieldName = "value",       DisplayName = "Value",           DataType = "number", Width = 100, IsEditable = true, EditType = "number", FormatString = "$0,000" },
            new() { FieldName = "stage",       DisplayName = "Stage",           DataType = "enum",   Width = 130, IsEditable = true, EditType = "dropdown", EditOptions = [..StageOptions], EmptyPlaceholder = "⚠ MISSING" },
            new() { FieldName = "owner",       DisplayName = "Owner",           DataType = "enum",   Width = 150, IsEditable = true, EditType = "dropdown", EditOptions = [..OwnerOptions], EmptyPlaceholder = "⚠ MISSING" },
            new() { FieldName = "probability", DisplayName = "Probability %",   DataType = "number", Width = 110, IsEditable = true, EditType = "number" },
            new() { FieldName = "days",        DisplayName = "Days Inactive",   DataType = "number", Width = 110, IsEditable = false },
            new() { FieldName = "status",      DisplayName = "Status",          DataType = "text",   Width = 110, IsEditable = false }
        ],
        Rows = opps.Select(BuildRow).ToList(),
        State = new() { TotalRows = opps.Count, PageSize = 30, HasVirtualization = true }
    };

    private GridRow BuildRow(Opportunity opp)
    {
        var isStale    = opp.DaysSinceActivity >= 60;
        var hasMissing = string.IsNullOrEmpty(opp.Stage) || string.IsNullOrEmpty(opp.Owner);

        string rowClass;
        string statusText;
        if (opp.IsApproved)        { rowClass = "row-approved"; statusText = "✓ Reviewed"; }
        else if (opp.IsInPreview)  { rowClass = "row-preview";  statusText = "⏳ Pending"; }
        else if (isStale)          { rowClass = "row-stale";    statusText = "🕐 Stale"; }
        else if (hasMissing)       { rowClass = "row-issue";    statusText = "⚠ Issues"; }
        else                       { rowClass = "row-ok";       statusText = "OK"; }

        return new GridRow
        {
            RowId        = opp.Id,
            IsEditable   = !opp.IsApproved,
            RowCssClass  = rowClass,
            Values = new Dictionary<string, object?>
            {
                ["customer"]    = opp.Customer,
                ["value"]       = $"${opp.Value:N0}",
                ["stage"]       = opp.Stage,
                ["owner"]       = opp.Owner,
                ["probability"] = $"{opp.Probability:F0}%",
                ["days"]        = opp.DaysSinceActivity,
                ["status"]      = statusText
            },
            CellCssClasses = new Dictionary<string, string>
            {
                ["stage"]       = string.IsNullOrEmpty(opp.Stage) ? "cell-missing" : "cell-ok",
                ["owner"]       = string.IsNullOrEmpty(opp.Owner) ? "cell-missing" : "cell-ok",
                ["probability"] = opp.Probability < 20 ? "cell-low" : "cell-ok",
                ["days"]        = isStale ? "cell-stale" : "cell-ok"
            }
        };
    }

    /// <summary>
    /// Builds a waterfall chart contract comparing starting forecast vs. current state.
    /// </summary>
    public ChartDataContract BuildChartContract(List<Opportunity> opps, decimal startingForecast)
    {
        var currentForecast = opps.Sum(o => o.Value * (decimal)(o.Probability / 100.0));
        var approvedValue   = opps.Where(o => o.IsApproved).Sum(o => o.Value * (decimal)(o.Probability / 100.0));
        var pendingValue    = opps.Where(o => !o.IsApproved).Sum(o => o.Value * (decimal)(o.Probability / 100.0));

        return new ChartDataContract
        {
            ChartType = "bar",
            Title     = "Forecast Waterfall",
            Series    =
            [
                new()
                {
                    Name     = "Forecast ($M)",
                    DataType = "bar",
                    Color    = "#2196f3",
                    DataPoints =
                    [
                        new() { Label = "Starting",  Value = (double)startingForecast / 1_000_000, DisplayValue = $"${startingForecast / 1_000_000:F2}M", Color = "#607d8b" },
                        new() { Label = "Reviewed",  Value = (double)approvedValue    / 1_000_000, DisplayValue = $"${approvedValue    / 1_000_000:F2}M", Color = "#4caf50" },
                        new() { Label = "Remaining", Value = (double)pendingValue     / 1_000_000, DisplayValue = $"${pendingValue     / 1_000_000:F2}M", Color = "#ff9800" },
                        new() { Label = "Current",   Value = (double)currentForecast  / 1_000_000, DisplayValue = $"${currentForecast  / 1_000_000:F2}M", Color = "#2196f3" }
                    ]
                }
            ],
            Config = new() { ShowLegend = true, ShowTooltip = true }
        };
    }

    /// <summary>
    /// Builds a gauge contract reflecting data quality confidence.
    /// </summary>
    public GaugeDataContract BuildGaugeContract(List<Opportunity> opps)
    {
        var confidence = CalculateConfidence(opps);
        return new GaugeDataContract
        {
            Title = "Pipeline Confidence",
            BeforeValue = new()
            {
                NumericValue = 47,
                DisplayValue = "47%",
                Label        = "Before Review",
                Subtitle     = "Low trust"
            },
            CurrentValue = new()
            {
                NumericValue = confidence,
                DisplayValue = $"{confidence:F0}%",
                Label        = "After Review",
                Subtitle     = confidence >= 66 ? "High trust" : confidence >= 33 ? "Medium trust" : "Low trust"
            },
            Range = new() { Min = 0, Max = 100, MajorStep = 10 },
            ColorRanges =
            [
                new() { From = 0,  To = 33,  Color = "#f44336" },
                new() { From = 33, To = 66,  Color = "#ff9800" },
                new() { From = 66, To = 100, Color = "#4caf50" }
            ]
        };
    }

    /// <summary>
    /// Builds a summary panel contract with key forecast metrics.
    /// </summary>
    public PanelDataContract BuildPanelContract(List<Opportunity> opps, decimal startingForecast)
    {
        var current = opps.Sum(o => o.Value * (decimal)(o.Probability / 100.0));
        var change  = current - startingForecast;
        var issues  = opps.Count(o => !o.IsApproved && (string.IsNullOrEmpty(o.Stage) || string.IsNullOrEmpty(o.Owner) || o.DaysSinceActivity >= 60));
        var reviewed = opps.Count(o => o.IsApproved);

        return new PanelDataContract
        {
            Title = "Forecast Summary",
            Fields =
            [
                new() { Label = "Before",   Value = $"${startingForecast / 1_000_000:F2}M", DataType = "metric",    CssClass = "metric-before", FontSize = "large" },
                new() { Label = "Current",  Value = $"${current          / 1_000_000:F2}M", DataType = "metric",    CssClass = "metric-after",  FontSize = "large" },
                new() { Label = "Change",   Value = $"{(change >= 0 ? "+" : "")}${change / 1_000_000:F2}M", DataType = "highlight", CssClass = change >= 0 ? "positive" : "negative", FontSize = "medium" },
                new() { Label = "Progress", Value = $"{reviewed} / {opps.Count} reviewed",  DataType = "text",      CssClass = "metric-progress", FontSize = "small" },
                new() { Label = "Issues",   Value = $"{issues} remaining",                   DataType = "text",      CssClass = issues == 0 ? "positive" : "negative", FontSize = "small" }
            ]
        };
    }

    /// <summary>
    /// Builds a line chart contract showing mock improvement trend over time.
    /// </summary>
    public ChartDataContract BuildTrendChartContract(List<Opportunity> opps)
    {
        var currentConfidence = CalculateConfidence(opps);
        
        return new ChartDataContract
        {
            ChartType = "line",
            Title     = "Data Quality Trend",
            Series    =
            [
                new()
                {
                    Name     = "Quality Score %",
                    DataType = "line",
                    Color    = "#00e5ff",
                    DataPoints =
                    [
                        new() { Label = "Oct", Value = 32, DisplayValue = "32%" },
                        new() { Label = "Nov", Value = 35, DisplayValue = "35%" },
                        new() { Label = "Dec", Value = 31, DisplayValue = "31%" },
                        new() { Label = "Jan", Value = 38, DisplayValue = "38%" },
                        new() { Label = "Feb", Value = 47, DisplayValue = "47%" },
                        new() { Label = "Mar", Value = currentConfidence, DisplayValue = $"{currentConfidence:F0}%", Color = "#00e5ff" }
                    ]
                }
            ],
            Config = new() { ShowLegend = true, ShowTooltip = true }
        };
    }

    private static double CalculateConfidence(List<Opportunity> opps)
    {
        if (!opps.Any()) return 47;

        var scorePerOpp = opps.Select(o =>
        {
            double score = 47.0;
            if (!string.IsNullOrEmpty(o.Stage))  score += 10;
            if (!string.IsNullOrEmpty(o.Owner))  score += 8;
            if (o.DaysSinceActivity < 60)         score += 6;
            if (o.IsApproved)                     score += 5;
            if (o.Probability >= 30)              score += 4;
            return Math.Min(score, 95);
        });

        var avg = scorePerOpp.Average();
        return Math.Round(Math.Min(avg, 95), 1);
    }
}
