namespace SalesPipelineDemo.Client.Models;

// ─── Grid ────────────────────────────────────────────────────────────────────

public class GridDataContract
{
    public string Title { get; set; } = "";
    public List<ColumnDefinition> Columns { get; set; } = [];
    public List<GridRow> Rows { get; set; } = [];
    public GridState State { get; set; } = new();
}

public class ColumnDefinition
{
    public string FieldName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DataType { get; set; } = "text";
    public int Width { get; set; } = 120;
    public bool IsEditable { get; set; }
    public string? EditType { get; set; }
    public List<string> EditOptions { get; set; } = [];
    public string? FormatString { get; set; }
    public string? EmptyPlaceholder { get; set; }
}

public class GridRow
{
    public string RowId { get; set; } = "";
    public Dictionary<string, object?> Values { get; set; } = [];
    public string RowCssClass { get; set; } = "row";
    public Dictionary<string, string> CellCssClasses { get; set; } = [];
    public bool IsEditable { get; set; } = true;
    public string? EditingCellField { get; set; }
}

public class GridState
{
    public int TotalRows { get; set; }
    public int PageSize { get; set; } = 50;
    public bool HasVirtualization { get; set; } = true;
}

// ─── Chart ───────────────────────────────────────────────────────────────────

public class ChartDataContract
{
    public string ChartType { get; set; } = "bar";
    public string Title { get; set; } = "";
    public List<ChartSeries> Series { get; set; } = [];
    public ChartConfiguration Config { get; set; } = new();
}

public class ChartSeries
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "bar";
    public string Color { get; set; } = "#2196f3";
    public List<ChartDataPoint> DataPoints { get; set; } = [];
}

public class ChartDataPoint
{
    public string Label { get; set; } = "";
    public double Value { get; set; }
    public string DisplayValue { get; set; } = "";
    public string Color { get; set; } = "#2196f3";
}

public class ChartConfiguration
{
    public bool ShowLegend { get; set; } = true;
    public string LegendPosition { get; set; } = "bottom";
    public bool ShowTooltip { get; set; } = true;
}

// ─── Gauge ───────────────────────────────────────────────────────────────────

public class GaugeDataContract
{
    public string Title { get; set; } = "";
    public GaugeValue? BeforeValue { get; set; }
    public GaugeValue? CurrentValue { get; set; }
    public GaugeRange Range { get; set; } = new();
    public List<GaugeColorRange> ColorRanges { get; set; } = [];
}

public class GaugeValue
{
    public double NumericValue { get; set; }
    public string DisplayValue { get; set; } = "";
    public string Label { get; set; } = "";
    public string Subtitle { get; set; } = "";
}

public class GaugeRange
{
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double MajorStep { get; set; } = 10;
}

public class GaugeColorRange
{
    public double From { get; set; }
    public double To { get; set; }
    public string Color { get; set; } = "";
}

// ─── Panel ───────────────────────────────────────────────────────────────────

public class PanelDataContract
{
    public string Title { get; set; } = "";
    public List<PanelField> Fields { get; set; } = [];
}

public class PanelField
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string DataType { get; set; } = "text";
    public string CssClass { get; set; } = "";
    public string FontSize { get; set; } = "medium";
}

// ─── UI State ────────────────────────────────────────────────────────────────

public class UIState
{
    public GridDataContract GridContract { get; set; } = new();
    public ChartDataContract ChartContract { get; set; } = new();
    public GaugeDataContract GaugeContract { get; set; } = new();
    public PanelDataContract PanelContract { get; set; } = new();
    public bool IsPreviewMode { get; set; }
    public int ReviewedCount { get; set; }
    public int TotalIssues { get; set; }
    public DateTime Timestamp { get; set; }
}

// ─── Hub messages ────────────────────────────────────────────────────────────

public class GridEditEvent
{
    public string RowId { get; set; } = "";
    public string FieldName { get; set; } = "";
    public object? OldValue { get; set; }
    public object? NewValue { get; set; }
}

public class BulkOperationRequest
{
    public string Criteria { get; set; } = "";
    public string TargetField { get; set; } = "";
    public object? NewValue { get; set; }
}

public class PlannedChange
{
    public string OpportunityId { get; set; } = "";
    public string FieldName { get; set; } = "";
    public object? OldValue { get; set; }
    public object? NewValue { get; set; }
}

public class BulkOperationPreview
{
    public int MatchingCount { get; set; }
    public UIState PreviewState { get; set; } = new();
    public List<PlannedChange> PlannedChanges { get; set; } = [];
}

// ─── AG-UI Chat ───────────────────────────────────────────────────────────────

public class ChatMessage
{
    public string Role { get; set; } = "user";   // user | agent
    public string Content { get; set; } = "";
    public bool IsStreaming { get; set; }
    public List<ToolCallInfo> ToolCalls { get; set; } = [];
    public List<AgentStatusEvent> StatusEvents { get; set; } = [];
    public List<RenderComponentRequest> RenderRequests { get; set; } = [];
}

public class ToolCallInfo
{
    public string CallId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string ToolName { get; set; } = "";
    public string Args { get; set; } = "{}";
    public string? Result { get; set; }   // populated when the tool result arrives
}

public class AgentStatusEvent
{
    public string Step { get; set; } = "";    // e.g. "llm_call", "tool_call", "tool_result"
    public string Detail { get; set; } = "";  // human-readable label
    public DateTime At { get; set; } = DateTime.Now;
}

// ─── Dynamic Canvas ───────────────────────────────────────────────────────────

public class RenderComponentRequest
{
    public string ComponentType { get; set; } = "";  // "grid" | "chart" | "gauge" | "panel"
    public string DataKey { get; set; } = "";         // "all" | "stale" | "missing-owner" | "missing-stage"
    public string Title { get; set; } = "";
}

public class ComponentBundle
{
    public RenderComponentRequest Request { get; set; } = new();
    public string ComponentType => Request.ComponentType;
    public GridDataContract? GridContract { get; set; }
    public ChartDataContract? ChartContract { get; set; }
    public GaugeDataContract? GaugeContract { get; set; }
    public PanelDataContract? PanelContract { get; set; }
}

public class ComponentDataRequest
{
    public string ComponentType { get; set; } = "";
    public string DataKey { get; set; } = "";
    public string Title { get; set; } = "";
}

public class ComponentDataResponse
{
    public string ComponentType { get; set; } = "";
    public string DataKey { get; set; } = "";
    public string Title { get; set; } = "";
    public GridDataContract? GridContract { get; set; }
    public ChartDataContract? ChartContract { get; set; }
    public GaugeDataContract? GaugeContract { get; set; }
    public PanelDataContract? PanelContract { get; set; }
}
