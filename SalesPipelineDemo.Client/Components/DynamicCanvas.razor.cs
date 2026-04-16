using Microsoft.AspNetCore.Components;
using SalesPipelineDemo.Client.Models;

namespace SalesPipelineDemo.Client.Components;

public partial class DynamicCanvas
{
    [Parameter] public EventCallback<GridEditEvent> OnCellEdit { get; set; }
    [Parameter] public EventCallback OnSubmit { get; set; }
    /// <summary>
    /// Delegate the canvas uses to re-fetch a single component's data.
    /// Provided by the parent (SalesQualityReview) which calls hub.FetchComponentData.
    /// Used by RefreshAsync on SignalR StateUpdated, and by the "Recall UI" path.
    /// </summary>
    [Parameter] public Func<ComponentDataRequest, Task<ComponentDataResponse>>? FetchComponent { get; set; }

    // key = "componentType:dataKey"
    private readonly Dictionary<string, ComponentBundle> _slots = new(StringComparer.Ordinal);

    /// <summary>
    /// Called when the agent emits render_component tool results.
    /// Merges the new responses into the existing slots so components accumulate
    /// across multiple tool calls in the same agent turn.
    /// Pass clearFirst=true only when starting a fresh agent turn.
    /// </summary>
    public void SetComponents(List<ComponentDataResponse> responses, bool clearFirst = false)
    {
        if (clearFirst) _slots.Clear();
        foreach (var resp in responses)
        {
            var key = $"{resp.ComponentType}:{resp.DataKey}";
            _slots[key] = new ComponentBundle
            {
                Request       = new RenderComponentRequest { ComponentType = resp.ComponentType, DataKey = resp.DataKey, Title = resp.Title },
                GridContract  = resp.GridContract,
                ChartContract = resp.ChartContract,
                GaugeContract = resp.GaugeContract,
                PanelContract = resp.PanelContract
            };
        }
        StateHasChanged();
    }

    /// <summary>
    /// Re-fetches data for all existing slots via the FetchComponent delegate.
    /// Called by the parent on SignalR StateUpdated so the canvas stays fresh
    /// after inline edits, bulk approvals, or submit review.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (!_slots.Any() || FetchComponent is null) return;
        var tasks = _slots.Values.Select(b => FetchSlotAsync(b.Request));
        await Task.WhenAll(tasks);
        StateHasChanged();
    }

    /// <summary>
    /// Re-fetches a specific set of requests (used by "Recall UI").
    /// Replaces current slots with the recalled set.
    /// </summary>
    public async Task RecallAsync(List<RenderComponentRequest> requests)
    {
        if (FetchComponent is null) return;
        _slots.Clear();
        foreach (var req in requests)
            _slots[$"{req.ComponentType}:{req.DataKey}"] = new ComponentBundle { Request = req };
        StateHasChanged();
        var tasks = requests.Select(r => FetchSlotAsync(r));
        await Task.WhenAll(tasks);
        StateHasChanged();
    }

    /// <summary>
    /// Applies preview grid data to all grid slots.
    /// If no grid slot exists yet, creates an "all" grid slot so the preview is visible.
    /// </summary>
    public void ApplyPreviewGridState(GridDataContract previewGrid)
    {
        var hasGridSlot = false;
        foreach (var (_, bundle) in _slots)
        {
            if (bundle.ComponentType == "grid")
            {
                bundle.GridContract = previewGrid;
                hasGridSlot = true;
            }
        }

        if (!hasGridSlot)
        {
            var key = "grid:all";
            _slots[key] = new ComponentBundle
            {
                Request      = new RenderComponentRequest { ComponentType = "grid", DataKey = "all", Title = previewGrid.Title },
                GridContract = previewGrid
            };
        }

        StateHasChanged();
    }

    private async Task FetchSlotAsync(RenderComponentRequest req)
    {
        if (FetchComponent is null) return;
        try
        {
            var response = await FetchComponent(new ComponentDataRequest
            {
                ComponentType = req.ComponentType,
                DataKey       = req.DataKey,
                Title         = req.Title
            });

            var key = $"{req.ComponentType}:{req.DataKey}";
            if (_slots.TryGetValue(key, out var bundle))
            {
                bundle.GridContract  = response.GridContract;
                bundle.ChartContract = response.ChartContract;
                bundle.GaugeContract = response.GaugeContract;
                bundle.PanelContract = response.PanelContract;
            }
        }
        catch { /* ignore transient errors */ }
    }

    private void RemoveSlot(string key)
    {
        _slots.Remove(key);
        StateHasChanged();
    }

    private async Task HandleCellEdit(GridEditEvent edit) =>
        await OnCellEdit.InvokeAsync(edit);

    private async Task HandleSubmit() =>
        await OnSubmit.InvokeAsync();
}
