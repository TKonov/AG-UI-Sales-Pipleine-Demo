using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using SalesPipelineDemo.Client.Models;
using SalesPipelineDemo.Client.Components;
using System.Text.Json;

namespace SalesPipelineDemo.Client.Pages;

public partial class SalesQualityReview : IAsyncDisposable
{
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IConfiguration Config { get; set; } = default!;

    private HubConnection? _hub;
    private AgentChat? _chat;
    private DynamicCanvas? _canvas;
    private string? _error;
    private bool _isPreviewMode;
    private GridDataContract? _latestPreviewGrid;

    private string ApiBase => Config["ApiBase"] ?? "https://localhost:7001";

    protected override async Task OnInitializedAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl($"{ApiBase}/hubs/sales-quality")
            .WithAutomaticReconnect()
            .Build();

        // SignalR: state changed (cell edit, bulk approve, submit review)
        // Re-fetch all current canvas slots via hub so they show fresh data.
        _hub.On<JsonElement>("StateUpdated", async (_) =>
        {
            _isPreviewMode     = false;
            _latestPreviewGrid = null;
            if (_canvas is not null)
                await _canvas.RefreshAsync();
            await InvokeAsync(StateHasChanged);
        });

        // SignalR: bulk_operation preview broadcast from the agent's hub call.
        // The agent now calls IHubContext.Clients.All internally, so this fires
        // automatically — the client just applies the preview grid.
        _hub.On<JsonElement>("BulkOperationPreview", async (element) =>
        {
            var preview = JsonSerializer.Deserialize<BulkOperationPreview>(element.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (preview is null) return;

            _isPreviewMode     = true;
            _latestPreviewGrid = preview.PreviewState.GridContract;
            _canvas?.ApplyPreviewGridState(preview.PreviewState.GridContract);
            _chat?.SetPendingBulk(preview);
            await InvokeAsync(StateHasChanged);
        });

        _hub.On<string>("Error", async (msg) =>
        {
            _error = msg;
            await InvokeAsync(StateHasChanged);
            await Task.Delay(4000);
            _error = null;
            await InvokeAsync(StateHasChanged);
        });

        _hub.On<int>("SubmitResult", async (count) =>
        {
            if (_chat is not null)
                await _chat.AddMessageAsync($"Submitted {count} reviewed opportunit{(count == 1 ? "y" : "ies")} — marked as ✓ Reviewed.");
            if (_canvas is not null)
                await _canvas.RefreshAsync();
            await InvokeAsync(StateHasChanged);
        });

        try
        {
            await _hub.StartAsync();
            await _hub.InvokeAsync("StartAnalysis");
        }
        catch (Exception ex)
        {
            _error = $"Could not connect to API: {ex.Message}";
        }
    }

    // ── AG-UI callbacks ───────────────────────────────────────────────────────

    /// <summary>
    /// Agent rendered components — data is already embedded in each response.
    /// Push directly to canvas; no hub fetch needed.
    /// </summary>
    private async Task HandleComponentDataReceived(List<ComponentDataResponse> responses)
    {
        if (_canvas is null) return;
        // clearFirst=true so a new agent turn replaces the previous canvas layout
        _canvas.SetComponents(responses, clearFirst: true);
        if (_isPreviewMode && _latestPreviewGrid is not null)
            _canvas.ApplyPreviewGridState(_latestPreviewGrid);
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// "Recall UI" — user wants to restore visualizations from a prior message.
    /// Re-fetch via hub (data may have changed since the message was rendered).
    /// </summary>
    private async Task HandleRecallView(List<RenderComponentRequest> requests)
    {
        if (_canvas is null || _hub?.State != HubConnectionState.Connected) return;
        await _canvas.RecallAsync(requests);
        await InvokeAsync(StateHasChanged);
    }

    // ── Delegate passed to DynamicCanvas for RefreshAsync / RecallAsync ───────

    private async Task<ComponentDataResponse> FetchComponentData(ComponentDataRequest request)
    {
        if (_hub?.State != HubConnectionState.Connected)
            return new ComponentDataResponse { ComponentType = request.ComponentType, DataKey = request.DataKey };
        return await _hub.InvokeAsync<ComponentDataResponse>("FetchComponentData", request);
    }

    // ── SignalR hub callbacks (state mutations) ───────────────────────────────

    private async Task HandleCellEdit(GridEditEvent edit)
    {
        if (_hub?.State != HubConnectionState.Connected) return;
        await _hub.InvokeAsync("UpdateCell", edit);
    }

    private async Task HandleBulkApproved(List<PlannedChange> changes)
    {
        if (_hub?.State != HubConnectionState.Connected) return;
        await _hub.InvokeAsync("ApproveBulkOperation", changes);
    }

    private async Task HandleBulkCancelled()
    {
        if (_hub?.State != HubConnectionState.Connected) return;
        await _hub.InvokeAsync("CancelBulkOperation");
    }

    private async Task HandleSubmitReview()
    {
        if (_hub?.State != HubConnectionState.Connected) return;
        await _hub.InvokeAsync("SubmitReview");
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
            await _hub.DisposeAsync();
    }
}
