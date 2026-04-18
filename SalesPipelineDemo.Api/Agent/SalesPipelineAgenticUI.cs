// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace SalesPipelineDemo.Api.Agent;

#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

internal sealed class SalesPipelineAgenticUI : DelegatingAIAgent
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public SalesPipelineAgenticUI(AIAgent innerAgent, JsonSerializerOptions? jsonSerializerOptions = null)
        : base(innerAgent)
    {
        this._jsonSerializerOptions = jsonSerializerOptions ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        return this.RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Track function calls that should trigger state events
        var trackedFunctionCalls = new Dictionary<string, FunctionCallContent>();

        await foreach (var update in this.InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
        {
            List<AIContent> stateEventsToEmit = new();

            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent callContent)
                {
                    // Track for result matching
                    trackedFunctionCalls[callContent.CallId] = callContent;

                    // Emit a status event for the tool call start
                    var label = BuildToolCallLabel(callContent.Name, callContent.Arguments);
                    var statusPayload = JsonSerializer.SerializeToUtf8Bytes(new { _type = "status", step = "tool_call", detail = label }, this._jsonSerializerOptions);
                    stateEventsToEmit.Add(new DataContent(statusPayload, "application/json"));
                }
                else if (content is FunctionResultContent resultContent)
                {
                    // Check if this result matches a tracked function call
                    if (trackedFunctionCalls.TryGetValue(resultContent.CallId, out var matchedCall))
                    {
                        if (matchedCall.Name == "render_component")
                        {
                            // Wrap in the envelope the client expects: { _type: "component", _callId: "...", data: { ... } }
                            var envelope = new
                            {
                                _type = "component",
                                _callId = resultContent.CallId,
                                data = resultContent.Result
                            };
                            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, this._jsonSerializerOptions);
                            stateEventsToEmit.Add(new DataContent(bytes, "application/json"));
                        }
                        else
                        {
                            // Emit a generic result event for other tools to show in the activity log
                            var resultPayload = JsonSerializer.SerializeToUtf8Bytes(new 
                            { 
                                _type = "status", 
                                step = "tool_result", 
                                detail = $"Result from {matchedCall.Name}: {resultContent.Result?.ToString()}" 
                            }, this._jsonSerializerOptions);
                            stateEventsToEmit.Add(new DataContent(resultPayload, "application/json"));
                        }
                    }
                }
            }

            yield return update;

            if (stateEventsToEmit.Count > 0)
            {
                yield return new AgentResponseUpdate(
                    new ChatResponseUpdate(role: ChatRole.System, stateEventsToEmit)
                    {
                        MessageId = "delta_" + Guid.NewGuid().ToString("N"),
                        CreatedAt = update.CreatedAt,
                        ResponseId = update.ResponseId,
                        AuthorName = update.AuthorName,
                        Role = update.Role,
                        ContinuationToken = update.ContinuationToken,
                        AdditionalProperties = update.AdditionalProperties,
                    })
                {
                    AgentId = update.AgentId
                };
            }
        }
    }

    private static string BuildToolCallLabel(string name, IDictionary<string, object?>? args)
    {
        string Arg(string k) => args?.TryGetValue(k, out var v) == true ? v?.ToString() ?? "" : "";
        return name switch
        {
            "render_component" => $"Rendering {Arg("componentType")} — {Arg("dataKey")}",
            "bulk_operation" => $"Staging bulk update: {Arg("criteria")} → {Arg("targetField")} = \"{Arg("newValue")}\"",
            "get_pipeline_stats" => "Reading pipeline statistics",
            _ => $"Calling tool: {name}"
        };
    }
}
