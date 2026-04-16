using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;
using SalesPipelineDemo.Client.Models;

namespace SalesPipelineDemo.Client.Services;

/// <summary>
/// AG-UI client using AGUIChatClient from the framework.
/// Converts the UI ChatMessage history into properly-typed Microsoft.Extensions.AI
/// ChatMessage objects so the AG-UI wire format includes tool call / tool result
/// turns, not just plain text.
///
/// Note: FunctionResultContent from the server is swallowed by FunctionInvokingChatClient
/// inside AGUIChatClient (NuGet 1.1.0-preview). Component data from render_component is
/// delivered via STATE_SNAPSHOT (DataContent application/json) with a "_type":"status"
/// discriminator for status events. The AIAgent/AgentThread pattern that would fix this
/// requires Microsoft.Agents.AI source packages not yet available as stable NuGet.
/// </summary>
public class AguiClient
{
    private readonly IChatClient _client;

    public AguiClient(HttpClient http)
    {
        _client = new AGUIChatClient(http, "/agents/sales-pipeline");
    }

    public async IAsyncEnumerable<ChatResponseUpdate> SendAsync(
        string userMessage,
        List<SalesPipelineDemo.Client.Models.ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Skip the in-progress streaming placeholder (last agent message while streaming).
        var messages = BuildMessages(history.Where(m => !m.IsStreaming).ToList());

        await foreach (var update in _client.GetStreamingResponseAsync(messages, null, ct))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Converts UI ChatMessage list to Microsoft.Extensions.AI ChatMessage list.
    /// For each agent message that has ToolCalls:
    ///   - Emits an Assistant ChatMessage with TextContent + FunctionCallContent per call
    ///   - Emits one Tool ChatMessage per call that has a Result
    /// </summary>
    private static List<Microsoft.Extensions.AI.ChatMessage> BuildMessages(
        List<SalesPipelineDemo.Client.Models.ChatMessage> uiMessages)
    {
        var result = new List<Microsoft.Extensions.AI.ChatMessage>();

        foreach (var msg in uiMessages)
        {
            if (msg.Role == "user")
            {
                result.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, msg.Content));
                continue;
            }

            if (msg.ToolCalls.Count > 0)
            {
                var assistantContents = new List<AIContent>();
                if (!string.IsNullOrEmpty(msg.Content))
                    assistantContents.Add(new TextContent(msg.Content));

                foreach (var tc in msg.ToolCalls)
                {
                    IDictionary<string, object?>? args = null;
                    try { args = JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.Args); }
                    catch { /* leave null */ }
                    assistantContents.Add(new FunctionCallContent(tc.CallId, tc.ToolName, args));
                }

                result.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, assistantContents));

                foreach (var tc in msg.ToolCalls.Where(t => t.Result is not null))
                {
                    result.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Tool,
                    [
                        new FunctionResultContent(tc.CallId, tc.Result!)
                    ]));
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(msg.Content))
                    result.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.Assistant, msg.Content));
            }
        }

        return result;
    }
}
