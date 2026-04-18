// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using Microsoft.AspNetCore.SignalR;
using SalesPipelineDemo.Api.Hubs;
using SalesPipelineDemo.Api.Models;
using SalesPipelineDemo.Api.Services;

namespace SalesPipelineDemo.Api.Agent;

public sealed class SalesPipelineTools
{
    private readonly OpportunityStore _store;
    private readonly MockContractService _builder;
    private readonly IHubContext<SalesQualityHub> _hubContext;
    private readonly ILogger<SalesPipelineTools> _logger;

    public SalesPipelineTools(
        OpportunityStore store,
        MockContractService builder,
        IHubContext<SalesQualityHub> hubContext,
        ILogger<SalesPipelineTools> logger)
    {
        _store = store;
        _builder = builder;
        _hubContext = hubContext;
        _logger = logger;
    }

    [Description("Renders a UI component on the right canvas panel. Call this to show data visually.")]
    public async Task<ComponentDataResponse> RenderComponentAsync(
        [Description("Component type: grid | chart | gauge | panel")] string componentType,
        [Description("Data filter: all | stale | missing-owner | missing-stage | trend")] string dataKey,
        [Description("Title shown above the component")] string title)
    {
        _logger.LogInformation("Tool Exec: render_component {Type} {Key}", componentType, dataKey);
        
        await Task.Delay(120); // Simulate work

        var all = _store.GetAll();
        var opps = dataKey switch
        {
            "stale" => all.Where(o => o.DaysSinceActivity >= 60).ToList(),
            "missing-owner" => all.Where(o => string.IsNullOrEmpty(o.Owner)).ToList(),
            "missing-stage" => all.Where(o => string.IsNullOrEmpty(o.Stage)).ToList(),
            _ => all
        };

        if (string.IsNullOrWhiteSpace(title))
        {
            title = dataKey switch
            {
                "stale" => $"Stale Deals — {componentType}",
                "missing-owner" => $"Missing Owners — {componentType}",
                "missing-stage" => $"Missing Stages — {componentType}",
                "trend" => "Data Quality Improvement Trend",
                _ => $"All Opportunities — {componentType}"
            };
        }

        var response = new ComponentDataResponse
        {
            ComponentType = componentType,
            DataKey = dataKey,
            Title = title
        };

        switch (componentType)
        {
            case "grid":
                var grid = _builder.BuildGridContract(opps);
                grid.Title = title;
                response.GridContract = grid;
                break;
            case "chart":
                var chart = dataKey == "trend"
                    ? _builder.BuildTrendChartContract(all)
                    : _builder.BuildChartContract(all, _store.StartingForecast);
                chart.Title = dataKey == "trend" ? title : "Global Forecast Waterfall";
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

        return response;
    }

    [Description("Queues a bulk data change for user approval. Previews affected rows in yellow before committing.")]
    public async Task<object> BulkOperationAsync(
        [Description("Filter for matching records: stale | missing-owner | missing-stage")] string criteria,
        [Description("Field to update: stage | owner")] string targetField,
        [Description("New value to set on all matching records")] string newValue)
    {
        _logger.LogInformation("Tool Exec: bulk_operation {Criteria} {Field} {Value}", criteria, targetField, newValue);
        
        await Task.Delay(500);
        var matching = _store.FindMatching(criteria);
        
        await Task.Delay(400);
        // Plan the changes
        var planned = matching.Select(opp => new PlannedChange
        {
            OpportunityId = opp.Id,
            FieldName = targetField,
            OldValue = opp.GetFieldValue(targetField),
            NewValue = newValue
        }).ToList();

        // Build preview state
        var allOpps = _store.GetAll();
        var preview = ApplyPreview(allOpps, planned);
        var previewState = BuildUIState(preview, isPreview: true);

        await Task.Delay(350);
        var bulkPreview = new BulkOperationPreview
        {
            MatchingCount = matching.Count,
            PlannedChanges = planned,
            PreviewState = previewState
        };
        
        await _hubContext.Clients.All.SendAsync("BulkOperationPreview", bulkPreview);

        return new
        {
            matchingCount = matching.Count,
            criteria,
            targetField,
            newValue
        };
    }

    [Description("Returns current pipeline statistics: counts, issues, forecast values.")]
    public async Task<object> GetPipelineStatsAsync()
    {
        _logger.LogInformation("Tool Exec: get_pipeline_stats");
        
        await Task.Delay(400);
        var all = _store.GetAll();
        
        await Task.Delay(500);
        await Task.Delay(350);

        return new
        {
            total = all.Count,
            reviewed = all.Count(o => o.IsApproved),
            missingStage = all.Count(o => string.IsNullOrEmpty(o.Stage) && !o.IsApproved),
            missingOwner = all.Count(o => string.IsNullOrEmpty(o.Owner) && !o.IsApproved),
            staleDeals = all.Count(o => o.DaysSinceActivity >= 60 && !o.IsApproved),
            totalIssues = all.Count(o => !o.IsApproved &&
                               (string.IsNullOrEmpty(o.Stage) ||
                                string.IsNullOrEmpty(o.Owner) ||
                                o.DaysSinceActivity >= 60)),
            forecastM = Math.Round(
                               (double)all.Sum(o => o.Value * (decimal)(o.Probability / 100.0)) / 1_000_000, 2)
        };
    }

    private UIState BuildUIState(List<Opportunity> opps, bool isPreview = false) => new()
    {
        GridContract = _builder.BuildGridContract(opps),
        ChartContract = _builder.BuildChartContract(opps, _store.StartingForecast),
        GaugeContract = _builder.BuildGaugeContract(opps),
        PanelContract = _builder.BuildPanelContract(opps, _store.StartingForecast),
        IsPreviewMode = isPreview,
        ReviewedCount = opps.Count(o => o.IsApproved),
        TotalIssues = opps.Count(o => !o.IsApproved && (string.IsNullOrEmpty(o.Stage) || string.IsNullOrEmpty(o.Owner) || o.DaysSinceActivity >= 60)),
        Timestamp = DateTime.UtcNow
    };

    private static List<Opportunity> ApplyPreview(List<Opportunity> opps, List<PlannedChange> changes)
    {
        foreach (var change in changes)
        {
            var opp = opps.FirstOrDefault(o => o.Id == change.OpportunityId);
            if (opp is null) continue;
            opp.SetFieldValue(change.FieldName, change.NewValue?.ToString());
            opp.IsInPreview = true;
        }
        return opps;
    }
}
