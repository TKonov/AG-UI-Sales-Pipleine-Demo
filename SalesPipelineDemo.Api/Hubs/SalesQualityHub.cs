using Microsoft.AspNetCore.SignalR;
using SalesPipelineDemo.Api.Models;
using SalesPipelineDemo.Api.Services;

namespace SalesPipelineDemo.Api.Hubs;

public class SalesQualityHub : Hub
{
    private readonly OpportunityStore _store;
    private readonly MockContractService _builder;
    private readonly ILogger<SalesQualityHub> _logger;

    public SalesQualityHub(OpportunityStore store, MockContractService builder, ILogger<SalesQualityHub> logger)
    {
        _store   = store;
        _builder = builder;
        _logger  = logger;
    }

    public async Task StartAnalysis()
    {
        _logger.LogInformation("StartAnalysis called from {ConnectionId}", Context.ConnectionId);
        var state = BuildUIState(_store.GetAll());
        await Clients.Caller.SendAsync("StateUpdated", state);
    }

    public async Task UpdateCell(GridEditEvent cellEdit)
    {
        _logger.LogInformation("UpdateCell {RowId}.{Field} = {Value}", cellEdit.RowId, cellEdit.FieldName, cellEdit.NewValue);

        _store.Update(cellEdit.RowId, cellEdit.FieldName, cellEdit.NewValue);

        var state = BuildUIState(_store.GetAll());
        await Clients.Caller.SendAsync("StateUpdated", state);
    }

    public async Task RequestBulkOperation(BulkOperationRequest request)
    {
        _logger.LogInformation("Bulk operation: {Criteria}", request.Criteria);

        var matching = _store.FindMatching(request.Criteria);
        if (!matching.Any())
        {
            await Clients.Caller.SendAsync("Error", $"No opportunities match criteria '{request.Criteria}'.");
            return;
        }

        // Plan (don't apply yet)
        var planned = matching.Select(opp => new PlannedChange
        {
            OpportunityId = opp.Id,
            FieldName     = request.TargetField,
            OldValue      = opp.GetFieldValue(request.TargetField),
            NewValue      = request.NewValue
        }).ToList();

        // Build preview state in-memory
        var allOpps  = _store.GetAll();
        var preview  = ApplyPreview(allOpps, planned);
        var previewState = BuildUIState(preview, isPreview: true);

        await Clients.Caller.SendAsync("BulkOperationPreview", new BulkOperationPreview
        {
            MatchingCount  = matching.Count,
            PlannedChanges = planned,
            PreviewState   = previewState
        });
    }

    public async Task ApproveBulkOperation(List<PlannedChange> changes)
    {
        _logger.LogInformation("Approving {Count} changes", changes.Count);

        foreach (var change in changes)
            _store.Update(change.OpportunityId, change.FieldName, change.NewValue);

        var state = BuildUIState(_store.GetAll());
        await Clients.Caller.SendAsync("StateUpdated", state);
    }

    public async Task CancelBulkOperation()
    {
        var state = BuildUIState(_store.GetAll());
        await Clients.Caller.SendAsync("StateUpdated", state);
    }

    public Task<ComponentDataResponse> FetchComponentData(ComponentDataRequest request)
    {
        _logger.LogInformation("FetchComponentData {Type}/{Key}", request.ComponentType, request.DataKey);

        var all  = _store.GetAll();
        var opps = request.DataKey switch
        {
            "stale"         => all.Where(o => o.DaysSinceActivity >= 60).ToList(),
            "missing-owner" => all.Where(o => string.IsNullOrEmpty(o.Owner)).ToList(),
            "missing-stage" => all.Where(o => string.IsNullOrEmpty(o.Stage)).ToList(),
            _               => all
        };

        var title = string.IsNullOrWhiteSpace(request.Title) ? DefaultTitle(request) : request.Title;

        var response = new ComponentDataResponse
        {
            ComponentType = request.ComponentType,
            DataKey       = request.DataKey,
            Title         = title
        };

        switch (request.ComponentType)
        {
            case "grid":
                var grid = _builder.BuildGridContract(opps);
                grid.Title = title;
                response.GridContract = grid;
                break;
            case "chart":
                // Charts/Gauges/Panels always use the full 'all' list for global context
                var chart = request.DataKey == "trend" 
                    ? _builder.BuildTrendChartContract(all)
                    : _builder.BuildChartContract(all, _store.StartingForecast);
                chart.Title = request.DataKey == "trend" ? title : "Global Forecast Waterfall";
                response.ChartContract = chart;
                break;
            case "gauge":
                var gauge = _builder.BuildGaugeContract(all);
                gauge.Title = "Global Pipeline Confidence";
                response.GaugeContract = gauge;
                break;
            case "panel":
                var panel = _builder.BuildPanelContract(all, _store.StartingForecast);
                panel.Title = "Global Forecast Summary";
                response.PanelContract = panel;
                break;
        }

        return Task.FromResult(response);
    }

    private static string DefaultTitle(ComponentDataRequest r) => r.DataKey switch
    {
        "stale"         => $"Stale Deals — {r.ComponentType}",
        "missing-owner" => $"Missing Owners — {r.ComponentType}",
        "missing-stage" => $"Missing Stages — {r.ComponentType}",
        "trend"         => "Data Quality Improvement Trend",
        _               => $"All Opportunities — {r.ComponentType}"
    };

    /// <summary>
    /// Marks all opportunities that have no remaining data quality issues as approved.
    /// Called when the user clicks "Submit Review" after making manual inline edits.
    /// </summary>
    public async Task SubmitReview()
    {
        var all = _store.GetAll();
        var readyIds = all
            .Where(o => !o.IsApproved
                     && !string.IsNullOrEmpty(o.Stage)
                     && !string.IsNullOrEmpty(o.Owner)
                     && o.DaysSinceActivity < 60)
            .Select(o => o.Id)
            .ToList();

        foreach (var id in readyIds)
            _store.Update(id, "stage", _store.Get(id)?.Stage); // triggers IsApproved = true

        _logger.LogInformation("SubmitReview approved {Count} opportunities", readyIds.Count);

        var state = BuildUIState(_store.GetAll());
        await Clients.Caller.SendAsync("StateUpdated", state);
        await Clients.Caller.SendAsync("SubmitResult", readyIds.Count);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private UIState BuildUIState(List<Opportunity> opps, bool isPreview = false) => new()
    {
        GridContract  = _builder.BuildGridContract(opps),
        ChartContract = _builder.BuildChartContract(opps, _store.StartingForecast),
        GaugeContract = _builder.BuildGaugeContract(opps),
        PanelContract = _builder.BuildPanelContract(opps, _store.StartingForecast),
        IsPreviewMode = isPreview,
        ReviewedCount = opps.Count(o => o.IsApproved),
        TotalIssues   = opps.Count(o => !o.IsApproved && (string.IsNullOrEmpty(o.Stage) || string.IsNullOrEmpty(o.Owner) || o.DaysSinceActivity >= 60)),
        Timestamp     = DateTime.UtcNow
    };

    private static List<Opportunity> ApplyPreview(List<Opportunity> opps, List<PlannedChange> changes)
    {
        foreach (var change in changes)
        {
            var opp = opps.FirstOrDefault(o => o.Id == change.OpportunityId);
            if (opp is null) continue;
            opp.SetFieldValue(change.FieldName, change.NewValue);
            opp.IsInPreview = true;
        }
        return opps;
    }
}
