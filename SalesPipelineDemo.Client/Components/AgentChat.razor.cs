using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Microsoft.Extensions.AI;
using SalesPipelineDemo.Client.Models;
using SalesPipelineDemo.Client.Services;
using System.Text.Json;
using Models = SalesPipelineDemo.Client.Models;

namespace SalesPipelineDemo.Client.Components;

public partial class AgentChat
{
    [Inject] private AguiClient AguiClient { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public EventCallback<List<PlannedChange>> OnBulkApproved { get; set; }
    [Parameter] public EventCallback OnBulkCancelled { get; set; }
    /// <summary>
    /// Fired when the agent emits render_component tool results.
    /// Each item carries the full ComponentDataResponse (data already populated
    /// server-side) — the canvas no longer needs a separate hub fetch.
    /// </summary>
    [Parameter] public EventCallback<List<ComponentDataResponse>> OnComponentDataReceived { get; set; }
    /// <summary>
    /// Fired when the user clicks "Recall UI" on a previous agent message.
    /// The list contains the original render requests; the parent should
    /// re-fetch data via hub and call into the canvas with fresh responses.
    /// </summary>
    [Parameter] public EventCallback<List<RenderComponentRequest>> OnRecallView { get; set; }

    public List<Models.ChatMessage> Messages { get; private set; } = [];
    public BulkOperationPreview? PendingChanges { get; set; }
    public bool IsStreaming { get; private set; }

    private string _inputText = "";
    private ElementReference _messagesDiv;
    private readonly HashSet<string> _expandedActivity = [];

    // ── Workflow stepper ───────────────────────────────────────────────────────

    private sealed record WorkflowStep(string Label, string Prompt);

    private readonly List<WorkflowStep> _workflowSteps =
    [
        new("Analyze",     "Analyze my sales pipeline and show me an overview of all opportunities"),
        new("Stale Deals", "Find all stale deals inactive for 60+ days and prepare to close them as Lost"),
        new("Fix Owners",  "Show opportunities with missing owners and assign them all to John Smith"),
        new("Fix Stages",  "Show opportunities with missing stages and set them to Qualified"),
        new("Summary",     "Give me a final status summary and forecast comparison after all fixes"),
    ];

    private int _workflowStep = 0;

    /// <summary>
    /// Runs a predefined workflow step by sending a specific prompt to the agent.
    /// </summary>
    private async Task RunWorkflowStep(int idx)
    {
        if (IsStreaming) return;
        _workflowStep = idx;
        await SendMessage(_workflowSteps[idx].Prompt);
        // Advance to next step (unless last)
        if (_workflowStep < _workflowSteps.Count - 1)
            _workflowStep++;
        StateHasChanged();
    }

    /// <summary>
    /// Processes incoming AG-UI events.
    /// COMPONENT arrives as DataContent("application/json") → STATE_SNAPSHOT SSE event
    ///   → ChatResponseUpdate with AdditionalProperties["is_state_snapshot"]=true
    ///   → deserialized as ComponentDataResponse, fires OnComponentDataReceived
    /// STATUS is suppressed server-side (STATE_DELTA is not handled by the client library).
    /// Activity log entries are built from FunctionCallContent (TOOL_CALL_START/END) instead.
    /// </summary>
    public async Task SendMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsStreaming) return;

        _inputText = "";
        Messages.Add(new Models.ChatMessage { Role = "user", Content = text });
        IsStreaming = true;
        StateHasChanged();

        var agentMsg = new Models.ChatMessage { Role = "agent", Content = "", IsStreaming = true };
        Messages.Add(agentMsg);

        var pendingDataResponses = new List<ComponentDataResponse>();

        try
        {
            await foreach (var update in AguiClient.SendAsync(text, Messages))
            {
                var isStateSnapshot = update.AdditionalProperties?.TryGetValue("is_state_snapshot", out var snap) == true && snap is true;

                if (isStateSnapshot)
                {
                    var dc = update.Contents.OfType<DataContent>().FirstOrDefault();
                    if (dc is not null)
                    {
                        try
                        {
                            var json = System.Text.Encoding.UTF8.GetString(dc.Data.Span);
                            using var doc = JsonDocument.Parse(json);
                            var root = doc.RootElement;

                            var snapshotType = root.TryGetProperty("_type", out var typeEl) ? typeEl.GetString() : null;

                            if (snapshotType == "status")
                            {
                                // STATUS progress event — add to activity log
                                var step = root.TryGetProperty("step", out var s) ? s.GetString() ?? "info" : "info";
                                var detail = root.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
                                agentMsg.StatusEvents.Add(new AgentStatusEvent { Step = step, Detail = detail });
                                _expandedActivity.Add(agentMsg.GetHashCode().ToString());
                            }
                            else if (snapshotType == "component" &&
                                     root.TryGetProperty("data", out var dataEl) &&
                                     root.TryGetProperty("_callId", out var callIdEl))
                            {
                                // COMPONENT data — envelope with _callId for precise tool call matching
                                var callId = callIdEl.GetString();
                                var resp = JsonSerializer.Deserialize<ComponentDataResponse>(
                                    dataEl.GetRawText(),
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                if (resp is not null)
                                {
                                    pendingDataResponses.Add(resp);
                                    agentMsg.RenderRequests.Add(new RenderComponentRequest
                                    {
                                        ComponentType = resp.ComponentType,
                                        DataKey = resp.DataKey,
                                        Title = resp.Title
                                    });
                                    var tc = agentMsg.ToolCalls.FirstOrDefault(t => t.CallId == callId);
                                    if (tc is not null)
                                        tc.Result = $"Rendered {resp.ComponentType} for {resp.DataKey}";
                                }
                            }
                        }
                        catch { /* malformed snapshot */ }
                    }
                }
                else
                {
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case TextContent tc:
                                agentMsg.Content += tc.Text;
                                break;

                            case FunctionCallContent fcc:
                                var label = BuildToolCallLabel(fcc.Name, fcc.Arguments);
                                agentMsg.StatusEvents.Add(new AgentStatusEvent { Step = "tool_call", Detail = label });
                                _expandedActivity.Add(agentMsg.GetHashCode().ToString());
                                agentMsg.ToolCalls.Add(new ToolCallInfo
                                {
                                    CallId = fcc.CallId,
                                    ToolName = fcc.Name,
                                    Args = fcc.Arguments != null ? JsonSerializer.Serialize(fcc.Arguments) : "{}"
                                });
                                break;

                            case FunctionResultContent frc:
                                // Tool results reach the client for non-render tools.
                                // Populate tc.Result so BuildMessages can include them in history.
                                var tcResult = agentMsg.ToolCalls.FirstOrDefault(t => t.CallId == frc.CallId);
                                if (tcResult is not null && tcResult.Result is null)
                                    tcResult.Result = frc.Result?.ToString();
                                break;
                        }
                    }
                }

                StateHasChanged();
            }

            // Fire all collected component responses as a single batch so canvas
            // receives the complete set and calls SetComponents once.
            if (pendingDataResponses.Any() && OnComponentDataReceived.HasDelegate)
                await OnComponentDataReceived.InvokeAsync([.. pendingDataResponses]);
        }
        catch (Exception ex)
        {
            agentMsg.Content += $"\n\n Connection error: {ex.Message}";
        }
        finally
        {
            agentMsg.IsStreaming = false;
            IsStreaming = false;
            StateHasChanged();
        }
    }

    private static string BuildToolCallLabel(string name, IDictionary<string, object?>? args)
    {
        string Arg(string k) => args?.TryGetValue(k, out var v) == true ? v?.ToString() ?? "" : "";
        return name switch
        {
            "render_component" => $"Rendering {Arg("componentType")} — {Arg("dataKey")}",
            "bulk_operation" => $"Staging bulk: {Arg("criteria")} → {Arg("targetField")} = \"{Arg("newValue")}\"",
            "get_pipeline_stats" => "Reading pipeline statistics",
            _ => $"Calling {name}"
        };
    }

    public void SetPendingBulk(BulkOperationPreview preview)
    {
        PendingChanges = preview;
        StateHasChanged();
    }

    private async Task ApproveBulk()
    {
        if (PendingChanges is null) return;
        var changes = PendingChanges.PlannedChanges;
        PendingChanges = null;
        await OnBulkApproved.InvokeAsync(changes);
        AddAgentMessage($"Applied {changes.Count} changes. Grid and forecasts updated!");
        StateHasChanged();
    }

    private async Task CancelBulk()
    {
        PendingChanges = null;
        await OnBulkCancelled.InvokeAsync();
        AddAgentMessage("Bulk operation cancelled. No changes were made.");
        StateHasChanged();
    }

    private async Task SendCurrentMessage()
    {
        var text = _inputText;
        _inputText = "";
        await SendMessage(text);
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") await SendCurrentMessage();
    }

    private void ToggleActivity(string key)
    {
        if (!_expandedActivity.Add(key))
            _expandedActivity.Remove(key);
    }

    private static string StepIcon(string step) => step switch
    {
        "llm_call" => "psychology",
        "tool_call" => "build",
        "tool_exec" => "settings",
        "tool_result" => "task_alt",
        "done" => "flag",
        _ => "radio_button_unchecked"
    };

    private void AddAgentMessage(string content) =>
        Messages.Add(new Models.ChatMessage { Role = "agent", Content = content });

    public async Task AddMessageAsync(string content)
    {
        AddAgentMessage(content);
        StateHasChanged();
    }

    /// <summary>
    /// Re-requests the components that the agent originally rendered for a specific message.
    /// This allows restoring the visual state of the canvas for historical messages.
    /// </summary>
    private async Task RestoreView(Models.ChatMessage msg)
    {
        if (msg.RenderRequests.Any() && OnRecallView.HasDelegate)
            await OnRecallView.InvokeAsync(msg.RenderRequests);
    }

    private static string? ParseStringField(string json, string field)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(field, out var el) ? el.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Performs minimal markdown rendering for chat messages (bold, italic, lists, newlines).
    /// </summary>
    private static string RenderMarkdown(string md)
    {
        // Minimal markdown: **bold**, *italic*, newlines, bullet lists
        var html = System.Net.WebUtility.HtmlEncode(md);
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        html = System.Text.RegularExpressions.Regex.Replace(html, @"\*(.+?)\*", "<em>$1</em>");
        html = html.Replace("\n- ", "\n• ");
        html = html.Replace("\n", "<br/>");
        return html;
    }

    /// <summary>
    /// Formats a raw JSON string for pretty printing.
    /// </summary>
    private static string PrettyJson(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch { return raw; }
    }
}
