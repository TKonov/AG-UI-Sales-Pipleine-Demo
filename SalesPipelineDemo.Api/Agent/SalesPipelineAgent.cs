using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using OpenAI;
using SalesPipelineDemo.Api.Hubs;
using SalesPipelineDemo.Api.Models;
using SalesPipelineDemo.Api.Services;
using System.ClientModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SalesPipelineDemo.Api.Agent;

/// <summary>
/// IChatClient implementation backed by any OpenAI-compatible LLM.
///
/// Option C hybrid: AG-UI handles all chat/streaming, SignalR hub handles
/// state mutations and multi-user broadcasts. Tools that mutate state call
/// IHubContext&lt;SalesQualityHub&gt; internally so the hub's own logic runs once,
/// and all connected clients get the SignalR broadcast as normal.
///
/// Sentinels (<<<RENDER>>>, <<<BULK>>>, <<<STATUS>>>) are fully removed.
/// Tool results carry structured JSON payloads consumed by the client via
/// the AG-UI ToolCallResult event.
/// </summary>
public sealed class SalesPipelineChatClient : IChatClient
{
    private readonly OpportunityStore _store;
    private readonly MockContractService _builder;
    private readonly IHubContext<SalesQualityHub> _hubContext;
    private readonly ILogger<SalesPipelineChatClient> _logger;
    private readonly IChatClient _llm;

    // ── Tools the LLM can call ─────────────────────────────────────────────────

    private static readonly AIFunction RenderComponentTool = AIFunctionFactory.Create(
        ([Description("Component type: grid | chart | gauge | panel")] string componentType,
         [Description("Data filter: all | stale | missing-owner | missing-stage | trend")] string dataKey,
         [Description("Title shown above the component")] string title) =>
            $"render_component:{componentType}:{dataKey}:{title}",
        "render_component",
        "Renders a UI component on the right canvas panel. Call this to show data visually.");

    private static readonly AIFunction BulkOperationTool = AIFunctionFactory.Create(
        ([Description("Filter for matching records: stale | missing-owner | missing-stage")] string criteria,
         [Description("Field to update: stage | owner")] string targetField,
         [Description("New value to set on all matching records")] string newValue) =>
            $"bulk_operation:{criteria}:{targetField}:{newValue}",
        "bulk_operation",
        "Queues a bulk data change for user approval. Previews affected rows in yellow before committing.");

    private static readonly AIFunction GetStatsTool = AIFunctionFactory.Create(
        () => "get_stats",
        "get_pipeline_stats",
        "Returns current pipeline statistics: counts, issues, forecast values.");

    public SalesPipelineChatClient(
        OpportunityStore store,
        MockContractService builder,
        IHubContext<SalesQualityHub> hubContext,
        ILogger<SalesPipelineChatClient> logger,
        IConfiguration config)
    {
        _store      = store;
        _builder    = builder;
        _hubContext = hubContext;
        _logger     = logger;
        _llm        = BuildLlmClient(config);
    }

    private static IChatClient BuildLlmClient(IConfiguration config)
    {
        var section = config.GetSection("LLM");
        var baseUrl = section["BaseUrl"] ?? "https://api.openai.com/v1";
        var apiKey  = section["ApiKey"]  ?? throw new InvalidOperationException("LLM:ApiKey is required in appsettings");
        var model   = section["Model"]   ?? "gpt-4o-mini";

        var openAiClient = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

        return openAiClient
            .GetChatClient(model)
            .AsIChatClient();
    }

    // ── IChatClient boilerplate ────────────────────────────────────────────────

    public ChatClientMetadata Metadata { get; } = new("SalesPipelineAgent");

    public object? GetService(Type serviceType, object? key = null) =>
        serviceType.IsInstanceOfType(this) ? this : _llm.GetService(serviceType, key);

    public void Dispose() => _llm.Dispose();

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var parts = new List<AIContent>();
        await foreach (var u in GetStreamingResponseAsync(messages, options, cancellationToken))
            parts.AddRange(u.Contents);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, parts));
    }

    // ── Streaming implementation ───────────────────────────────────────────────

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var msgId   = Guid.NewGuid().ToString("N")[..8];
        var history = BuildHistory(messages);
        var opts    = BuildOptions();
        var round   = 0;

        _logger.LogInformation("LLM call — {Count} messages in history", history.Count);

        while (true)
        {
            round++;
            yield return Status("llm_call",
                round == 1 ? "Calling LLM — generating response…"
                           : $"Calling LLM — round {round} (processing tool results)…", msgId);

            var textBuffer    = new System.Text.StringBuilder();
            var toolCalls     = new Dictionary<string, (string Name, IDictionary<string, object?>? Args)>();
            var toolCallOrder = new List<string>();
            var seenToolIds   = new HashSet<string>();

            await foreach (var update in _llm.GetStreamingResponseAsync(history, opts, cancellationToken))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent tc:
                            textBuffer.Append(tc.Text);
                            yield return new ChatResponseUpdate
                            {
                                Role      = ChatRole.Assistant,
                                MessageId = msgId,
                                Contents  = [new TextContent(tc.Text)]
                            };
                            break;

                        case FunctionCallContent fc:
                            if (!toolCalls.ContainsKey(fc.CallId))
                                toolCallOrder.Add(fc.CallId);
                            toolCalls[fc.CallId] = (fc.Name, fc.Arguments);

                            if (seenToolIds.Add(fc.CallId))
                                yield return Status("tool_call",
                                    ToolCallLabel(fc.Name, fc.Arguments), msgId);
                            break;
                    }
                }
            }

            if (toolCalls.Count == 0)
            {
                yield return Status("done", "Response complete.", msgId);
                yield break;
            }

            // Add assistant message with all tool calls to history
            var assistantContents = new List<AIContent>();
            if (textBuffer.Length > 0)
                assistantContents.Add(new TextContent(textBuffer.ToString()));
            foreach (var callId in toolCallOrder)
            {
                var (name, args) = toolCalls[callId];
                assistantContents.Add(new FunctionCallContent(callId, name, args));
            }
            history.Add(new ChatMessage(ChatRole.Assistant, assistantContents));

            // Execute each tool — yield status updates and FunctionResultContent.
            // The ToolResult helper marks updates with __toolOutput so we can add
            // them to history as Tool messages without forwarding them twice.
            foreach (var callId in toolCallOrder)
            {
                var (name, args) = toolCalls[callId];
                var argsJson     = args is not null ? JsonSerializer.Serialize(args) : "{}";

                await foreach (var update in ExecuteToolStreaming(name, argsJson, callId, msgId, cancellationToken))
                {
                    if (update.AdditionalProperties?.TryGetValue("__toolOutput", out var toolOutput) == true
                        && update.AdditionalProperties.TryGetValue("__callId", out var cid) == true)
                    {
                        // Add result to history so LLM can see it next round
                        history.Add(new ChatMessage(ChatRole.Tool,
                        [
                            new FunctionResultContent(cid?.ToString() ?? callId, toolOutput?.ToString() ?? "done")
                        ]));
                        // Also yield the update so AG-UI emits a ToolCallResult SSE event to the client
                        yield return update;
                    }
                    else
                    {
                        yield return update;
                    }
                }
            }
        }
    }

    // ── Tool execution ────────────────────────────────────────────────────────
    //
    // Each tool yields Status TextContent updates as it progresses.
    // For render_component, a COMPONENT TextContent update carries the full
    // ComponentDataResponse JSON to the client (FunctionResultContent is eaten
    // by FunctionInvokingChatClient in AGUIChatClient before reaching the browser).
    // The ToolResult marker (with __toolOutput in AdditionalProperties) is used
    // only for appending to LLM history — it is not forwarded to the client as text.

    private async IAsyncEnumerable<ChatResponseUpdate> ExecuteToolStreaming(
        string name,
        string argsJson,
        string callId,
        string msgId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("Executing tool {Name}: {Args}", name, argsJson);

        JsonElement root;
        string? parseError = null;
        try   { root = JsonDocument.Parse(argsJson.Length > 0 ? argsJson : "{}").RootElement; }
        catch (Exception ex) { root = default; parseError = ex.Message; }

        if (parseError is not null)
        {
            yield return Status("tool_result", $"Parse error: {parseError}", msgId);
            yield return ToolResult(callId, msgId, JsonSerializer.Serialize(new { error = parseError }));
            yield break;
        }

        switch (name)
        {
            // ── render_component ─────────────────────────────────────────────
            // Builds the full ComponentDataResponse server-side and delivers it
            // via a STATE_SNAPSHOT (DataContent application/json). FunctionResultContent
            // is swallowed by FunctionInvokingChatClient inside AGUIChatClient (NuGet
            // 1.1.0-preview), so STATE_SNAPSHOT is the only path to the browser.
            case "render_component":
            {
                var componentType = GetStr(root, "componentType");
                var dataKey       = GetStr(root, "dataKey");
                var title         = GetStr(root, "title");

                yield return Status("tool_exec", $"Resolving component: {componentType} / {dataKey}…", msgId);
                await Task.Delay(120, ct);

                var response = BuildComponentResponse(componentType, dataKey, title);
                var json     = JsonSerializer.Serialize(response);

                yield return ComponentData(json, callId, msgId);
                yield return Status("tool_result", $"Canvas updated — showing {componentType} for \"{dataKey}\"", msgId);
                yield return ToolResult(callId, msgId, JsonSerializer.Serialize($"Rendered {componentType} for {dataKey}"));
                break;
            }

            // ── bulk_operation ───────────────────────────────────────────────
            // Calls the hub internally via IHubContext so RequestBulkOperation
            // runs exactly once with the hub's own logic, and the SignalR
            // BulkOperationPreview broadcast fires to the caller's connection.
            // The connection ID is passed via AdditionalProperties on RunAgentInput
            // and threaded through history's AdditionalProperties by the agent.
            case "bulk_operation":
            {
                var criteria    = GetStr(root, "criteria");
                var targetField = GetStr(root, "targetField");
                var newValue    = GetStr(root, "newValue");

                yield return Status("tool_exec", $"Querying records matching: {criteria}…", msgId);
                await Task.Delay(500, ct);

                var matching = _store.FindMatching(criteria);
                yield return Status("tool_exec", $"Found {matching.Count} matching records — building change plan…", msgId);
                await Task.Delay(400, ct);

                // Plan the changes
                var planned = matching.Select(opp => new PlannedChange
                {
                    OpportunityId = opp.Id,
                    FieldName     = targetField,
                    OldValue      = opp.GetFieldValue(targetField),
                    NewValue      = newValue
                }).ToList();

                // Build preview state
                var allOpps      = _store.GetAll();
                var preview      = ApplyPreview(allOpps, planned);
                var previewState = BuildUIState(preview, isPreview: true);

                yield return Status("tool_exec", $"Generating preview for {matching.Count} × {targetField} → \"{newValue}\"…", msgId);
                await Task.Delay(350, ct);

                // Broadcast via SignalR to all clients (Caller not available via IHubContext,
                // so we broadcast to All — in a real app you'd use a group per session)
                var bulkPreview = new BulkOperationPreview
                {
                    MatchingCount  = matching.Count,
                    PlannedChanges = planned,
                    PreviewState   = previewState
                };
                await _hubContext.Clients.All.SendAsync("BulkOperationPreview", bulkPreview, ct);

                yield return Status("tool_result", $"Preview ready — {matching.Count} rows staged for approval", msgId);
                yield return ToolResult(callId, msgId, JsonSerializer.Serialize(new
                {
                    matchingCount = matching.Count,
                    criteria,
                    targetField,
                    newValue
                }));
                break;
            }

            // ── get_pipeline_stats ───────────────────────────────────────────
            case "get_pipeline_stats":
            {
                yield return Status("tool_exec", "Connecting to CRM data source…", msgId);
                await Task.Delay(400, ct);

                yield return Status("tool_exec", "Aggregating opportunity records…", msgId);
                await Task.Delay(500, ct);

                var all = _store.GetAll();
                yield return Status("tool_exec", $"Loaded {all.Count} opportunities — running forecast model…", msgId);
                await Task.Delay(350, ct);

                var stats = new
                {
                    total        = all.Count,
                    reviewed     = all.Count(o => o.IsApproved),
                    missingStage = all.Count(o => string.IsNullOrEmpty(o.Stage) && !o.IsApproved),
                    missingOwner = all.Count(o => string.IsNullOrEmpty(o.Owner) && !o.IsApproved),
                    staleDeals   = all.Count(o => o.DaysSinceActivity >= 60 && !o.IsApproved),
                    totalIssues  = all.Count(o => !o.IsApproved &&
                                       (string.IsNullOrEmpty(o.Stage) ||
                                        string.IsNullOrEmpty(o.Owner) ||
                                        o.DaysSinceActivity >= 60)),
                    forecastM    = Math.Round(
                                       (double)all.Sum(o => o.Value * (decimal)(o.Probability / 100.0)) / 1_000_000, 2)
                };

                yield return Status("tool_result",
                    $"Stats ready — {stats.totalIssues} issues, ${stats.forecastM}M forecast", msgId);
                yield return ToolResult(callId, msgId, JsonSerializer.Serialize(stats));
                break;
            }

            default:
                yield return Status("tool_result", $"Unknown tool: {name}", msgId);
                yield return ToolResult(callId, msgId, JsonSerializer.Serialize(new { error = $"Unknown tool: {name}" }));
                break;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a ComponentDataResponse directly on the server — same logic as
    /// SalesQualityHub.FetchComponentData, so the canvas gets data inline in
    /// the tool result without a second SignalR round-trip.
    /// </summary>
    private ComponentDataResponse BuildComponentResponse(string componentType, string dataKey, string title)
    {
        var all  = _store.GetAll();
        var opps = dataKey switch
        {
            "stale"         => all.Where(o => o.DaysSinceActivity >= 60).ToList(),
            "missing-owner" => all.Where(o => string.IsNullOrEmpty(o.Owner)).ToList(),
            "missing-stage" => all.Where(o => string.IsNullOrEmpty(o.Stage)).ToList(),
            _               => all
        };

        if (string.IsNullOrWhiteSpace(title))
            title = DefaultTitle(componentType, dataKey);

        var response = new ComponentDataResponse
        {
            ComponentType = componentType,
            DataKey       = dataKey,
            Title         = title
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

    private static string DefaultTitle(string componentType, string dataKey) => dataKey switch
    {
        "stale"         => $"Stale Deals — {componentType}",
        "missing-owner" => $"Missing Owners — {componentType}",
        "missing-stage" => $"Missing Stages — {componentType}",
        "trend"         => "Data Quality Improvement Trend",
        _               => $"All Opportunities — {componentType}"
    };

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
            opp.SetFieldValue(change.FieldName, change.NewValue?.ToString());
            opp.IsInPreview = true;
        }
        return opps;
    }

    // Emits a COMPONENT update via DataContent("application/json") → STATE_SNAPSHOT on the wire.
    // Wraps the ComponentDataResponse in an envelope with _type and _callId so the client can
    // match each snapshot back to its originating render_component tool call by CallId.
    private static ChatResponseUpdate ComponentData(string componentJson, string callId, string msgId)
    {
        var envelope = JsonSerializer.Serialize(new
        {
            _type   = "component",
            _callId = callId,
            data    = JsonDocument.Parse(componentJson).RootElement
        });
        return new()
        {
            Role      = ChatRole.Assistant,
            MessageId = msgId,
            Contents  = [new DataContent(System.Text.Encoding.UTF8.GetBytes(envelope), "application/json")]
        };
    }

    // STATUS updates are delivered as StateSnapshotEvent (application/json) with a
    // "_type":"status" discriminator so the client can tell them apart from component data.
    // STATE_DELTA is avoided because the NuGet client library (1.1.0-preview) does not
    // include STATE_DELTA in its BaseEventJsonConverter Read() switch and throws on receipt.
    private static ChatResponseUpdate Status(string step, string detail, string msgId)
    {
        var payload = JsonSerializer.Serialize(new { _type = "status", step, detail });
        return new ChatResponseUpdate
        {
            Role      = ChatRole.Assistant,
            MessageId = msgId,
            Contents  = [new DataContent(System.Text.Encoding.UTF8.GetBytes(payload), "application/json")]
        };
    }

    // Emits a FunctionResultContent so the library converts it to a ToolCallResult
    // SSE event. Also appends to history via a special AdditionalProperties marker
    // so the LLM loop can see the result without a second emit.
    // The content MUST be valid JSON — the AG-UI library deserializes ToolCallResult.Content
    // as a JsonElement on the client side. Pass a JsonSerializer.Serialize()'d value here.
    private static ChatResponseUpdate ToolResult(string callId, string msgId, string validJson)
    {
        var u = new ChatResponseUpdate
        {
            Role      = ChatRole.Tool,
            MessageId = msgId,
            Contents  = [new FunctionResultContent(callId, validJson)]
        };
        u.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        u.AdditionalProperties["__toolOutput"] = validJson;
        u.AdditionalProperties["__callId"]     = callId;
        return u;
    }

    private List<ChatMessage> BuildHistory(IEnumerable<ChatMessage> incoming)
    {
        var all   = _store.GetAll();
        var stats = new
        {
            total        = all.Count,
            reviewed     = all.Count(o => o.IsApproved),
            missingStage = all.Count(o => string.IsNullOrEmpty(o.Stage) && !o.IsApproved),
            missingOwner = all.Count(o => string.IsNullOrEmpty(o.Owner) && !o.IsApproved),
            staleDeals   = all.Count(o => o.DaysSinceActivity >= 60 && !o.IsApproved),
            forecastM    = Math.Round((double)all.Sum(o => o.Value * (decimal)(o.Probability / 100.0)) / 1_000_000, 2)
        };

        var system = $"""
            You are a Sales Pipeline Quality Agent helping a sales manager review and fix their pipeline.

            CURRENT PIPELINE STATE:
            {JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true })}

            YOUR TOOLS:
            - render_component: show a data visualisation on the canvas (chart/gauge/panel/grid × all/stale/missing-owner/missing-stage)
            - bulk_operation: stage a bulk field update for user approval (criteria, targetField, newValue)
            - get_pipeline_stats: refresh live stats

            BEHAVIOUR:
            - Always call render_component before describing what you found — show first, then narrate.
            - For bulk fixes, call bulk_operation then explain the preview to the user.
            - Be concise. Use markdown bold for numbers and key terms.
            - When the user asks to "analyze", render grid(all)+chart(all)+gauge(all)+panel(all), then summarise issues.
            - When the user asks about stale deals, render grid+chart for "stale".
            - When the user asks about owners, render grid+panel for "missing-owner".
            - When the user asks about stages, render grid+panel for "missing-stage".
            - For status/summary, render grid(all)+chart(all)+gauge(all)+panel(all).
            """;

        var history = new List<ChatMessage> { new(ChatRole.System, system) };

        foreach (var m in incoming)
        {
            if (m.Role == ChatRole.System) continue;
            history.Add(m);
        }

        return history;
    }

    private static ChatOptions BuildOptions() => new()
    {
        Tools    = [RenderComponentTool, BulkOperationTool, GetStatsTool],
        ToolMode = ChatToolMode.Auto
    };

    private static string GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) ? p.GetString() ?? "" : "";

    private static string ToolCallLabel(string name, IDictionary<string, object?>? args)
    {
        if (args is null) return $"Calling tool: {name}";
        string Arg(string k) => args.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
        return name switch
        {
            "render_component" =>
                Arg("dataKey") == "trend"
                    ? "Visualizing data quality trend"
                    : $"Rendering {Arg("componentType")} — {Arg("dataKey")}",
            "bulk_operation" =>
                $"Staging bulk update: {Arg("criteria")} → {Arg("targetField")} = \"{Arg("newValue")}\"",
            "get_pipeline_stats" =>
                "Reading pipeline statistics",
            _ => $"Calling tool: {name}"
        };
    }
}
