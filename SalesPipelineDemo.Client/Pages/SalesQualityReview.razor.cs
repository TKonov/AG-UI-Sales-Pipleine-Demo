using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using SalesPipelineDemo.Client.Models;
using SalesPipelineDemo.Client.Components;
using SalesPipelineDemo.Client.Services;
using System.Text.Json;

namespace SalesPipelineDemo.Client.Pages;

public partial class SalesQualityReview
{
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IConfiguration Config { get; set; } = default!;
    [Inject] private AguiClient AguiClient { get; set; } = default!;

    private AgentChat? _chat;
    private DynamicCanvas? _canvas;
    private string? _error;
    private bool _isPreviewMode;
    private GridDataContract? _latestPreviewGrid;

    protected override async Task OnInitializedAsync()
    {
        // No SignalR setup needed. 
        // We'll rely on the initial agent message or manual triggers to populate the canvas.
    }

    // ── AG-UI callbacks ───────────────────────────────────────────────────────

    /// <summary>
    /// Handles full dashboard state snapshots from the agent.
    /// This replaces the SignalR "StateUpdated" and "BulkOperationPreview" events.
    /// </summary>
    private async Task HandleFullStateReceived(UIState state)
    {
        _isPreviewMode = state.IsPreviewMode;
        
        if (_isPreviewMode)
        {
            _latestPreviewGrid = state.GridContract;
            _canvas?.ApplyPreviewGridState(state.GridContract);
            
            // Extract planned changes for the pending bulk view
            // In a more robust system, this would be part of the shared state
        }
        else
        {
            _latestPreviewGrid = null;
            // Update all dashboard components from the state
            if (_canvas is not null)
            {
                var responses = new List<ComponentDataResponse>
                {
                    new() { ComponentType = "grid", DataKey = "all", Title = state.GridContract.Title, GridContract = state.GridContract },
                    new() { ComponentType = "chart", DataKey = "all", Title = state.ChartContract.Title, ChartContract = state.ChartContract },
                    new() { ComponentType = "gauge", DataKey = "all", Title = state.GaugeContract.Title, GaugeContract = state.GaugeContract },
                    new() { ComponentType = "panel", DataKey = "all", Title = state.PanelContract.Title, PanelContract = state.PanelContract }
                };
                _canvas.SetComponents(responses, clearFirst: true);
            }
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Agent rendered specific components.
    /// </summary>
    private async Task HandleComponentDataReceived(List<ComponentDataResponse> responses)
    {
        if (_canvas is null) return;
        _canvas.SetComponents(responses, clearFirst: true);
        if (_isPreviewMode && _latestPreviewGrid is not null)
            _canvas.ApplyPreviewGridState(_latestPreviewGrid);
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleRecallView(List<RenderComponentRequest> requests)
    {
        if (_canvas is null) return;
        await _canvas.RecallAsync(requests);
        await InvokeAsync(StateHasChanged);
    }

    // ── Direct Agent Calls (Replacing SignalR Hub Invokes) ─────────────────────

    private async Task HandleCellEdit(GridEditEvent edit)
    {
        // Instead of calling a hub, we send a "hidden" command to the agent.
        // We could also use a specialized direct-tool-call method if we extend AguiClient.
        await SendHiddenAgentCommand($"Update opportunity {edit.RowId} field {edit.FieldName} to {edit.NewValue}");
    }

    private async Task HandleBulkPreviewReceived(BulkOperationPreview preview)
    {
        _isPreviewMode = true;
        _latestPreviewGrid = preview.PreviewState.GridContract;
        _canvas?.ApplyPreviewGridState(preview.PreviewState.GridContract);
        await InvokeAsync(StateHasChanged);
    }

    private async Task HandleBulkApproved(List<PlannedChange> changes)
    {
        // For simplicity, we send a summary command.
        await SendHiddenAgentCommand("Approve the pending bulk operation");
    }

    private async Task HandleBulkCancelled()
    {
        await SendHiddenAgentCommand("Cancel the pending bulk operation");
    }

    private async Task HandleSubmitReview()
    {
        await SendHiddenAgentCommand("Submit my reviewed opportunities");
    }

    private async Task SendHiddenAgentCommand(string prompt)
    {
        if (_chat is not null)
        {
            // We can send it via the chat which will trigger the tool and then emit full_state
            await _chat.SendMessage(prompt);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Cleanup if needed
    }
}
