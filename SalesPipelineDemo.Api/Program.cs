using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;
using OpenAI;
using SalesPipelineDemo.Api.Agent;
using SalesPipelineDemo.Api.Hubs;
using SalesPipelineDemo.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddHttpClient().AddLogging();
builder.Services.AddSignalR();

// Singleton store: shared in-memory data across all connections
builder.Services.AddSingleton<OpportunityStore>();
builder.Services.AddSingleton<MockContractService>();

// Register the AG-UI services
builder.Services.AddAGUI();

// Register tools
builder.Services.AddSingleton<SalesPipelineTools>();

// CORS for local Blazor WASM dev
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy
            .WithOrigins("http://localhost:5174", "https://localhost:7174", "http://localhost:5000", "https://localhost:7000")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

var app = builder.Build();

app.UseCors();
app.UseHttpsRedirection();

// ── SignalR hub ───────────────────────────────────────────────────────────────
app.MapHub<SalesQualityHub>("/hubs/sales-quality");

// ── AG-UI endpoint ────────────────────────────────────────────────────────────
var store = app.Services.GetRequiredService<OpportunityStore>();
var tools = app.Services.GetRequiredService<SalesPipelineTools>();
var config = app.Services.GetRequiredService<IConfiguration>();

// 1. Build LLM IChatClient
var section = config.GetSection("LLM");
var baseUrl = section["BaseUrl"] ?? "https://api.openai.com/v1";
var apiKey = section["ApiKey"] ?? throw new InvalidOperationException("LLM:ApiKey is required in appsettings");
var model = section["Model"] ?? "gpt-4o-mini";

var openAiClient = new OpenAIClient(
    new System.ClientModel.ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });

IChatClient llmClient = openAiClient.GetChatClient(model).AsIChatClient();

// 2. Define Tools
var aiTools = new List<AITool>
{
    AIFunctionFactory.Create(tools.RenderComponentAsync, "render_component"),
    AIFunctionFactory.Create(tools.BulkOperationAsync, "bulk_operation"),
    AIFunctionFactory.Create(tools.GetPipelineStatsAsync, "get_pipeline_stats"),
    AIFunctionFactory.Create(tools.UpdateOpportunityAsync, "update_opportunity"),
    AIFunctionFactory.Create(tools.ApproveBulkOperationAsync, "approve_bulk"),
    AIFunctionFactory.Create(tools.CancelBulkOperationAsync, "cancel_bulk"),
    AIFunctionFactory.Create(tools.SubmitReviewAsync, "submit_review")
};

// 3. Build System Instructions
var all = store.GetAll();
var stats = new
{
    total = all.Count,
    reviewed = all.Count(o => o.IsApproved),
    missingStage = all.Count(o => string.IsNullOrEmpty(o.Stage) && !o.IsApproved),
    missingOwner = all.Count(o => string.IsNullOrEmpty(o.Owner) && !o.IsApproved),
    staleDeals = all.Count(o => o.DaysSinceActivity >= 60 && !o.IsApproved),
    forecastM = Math.Round((double)all.Sum(o => o.Value * (decimal)(o.Probability / 100.0)) / 1_000_000, 2)
};

var systemMessage = $"""
    You are a Sales Pipeline Quality Agent helping a sales manager review and fix their pipeline.

    CURRENT PIPELINE STATE:
    {System.Text.Json.JsonSerializer.Serialize(stats, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })}

    YOUR TOOLS:
    - render_component: show a data visualisation on the canvas (chart/gauge/panel/grid x all/stale/missing-owner/missing-stage)
    - bulk_operation: stage a bulk field update for user approval (criteria, targetField, newValue)
    - get_pipeline_stats: refresh live stats
    - update_opportunity: update a single field for a specific opportunity
    - approve_bulk: commit the pending bulk operation
    - cancel_bulk: cancel the pending bulk operation
    - submit_review: final submit of all reviewed opportunities

    BEHAVIOUR:
    - ALWAYS call render_component before describing what you found — show first, then narrate.
    - NEVER output data as Markdown tables. All data visualization MUST be done via render_component.
    - Keep your text responses concise and focused on high-level analysis, trends, and recommendations.
    - Use markdown bold for numbers and key terms, but avoid repetitive lists of data already visible on the canvas.
    - For bulk fixes, call bulk_operation then explain the preview to the user.
    - When the user asks to update an opportunity, use update_opportunity.
    - When the user confirms a bulk fix, use approve_bulk.
    - When the user cancels, use cancel_bulk.
    - When the user wants to submit everything, use submit_review.
    - When the user asks to "analyze", render grid(all)+chart(all)+gauge(all)+panel(all), then summarise issues.
    - When the user asks about stale deals, render grid+chart for "stale".
    - When the user asks about owners, render grid+panel for "missing-owner".
    - When the user asks about stages, render grid+panel for "missing-stage".
    - For status/summary, render grid(all)+chart(all)+gauge(all)+panel(all).
    """;

// 4. Create the AIAgent
AIAgent innerAgent = new ChatClientAgent(llmClient, instructions: systemMessage, tools: aiTools, name: "SalesPipelineAgent");

// 5. Wrap with AgenticUI for AG-UI event passing
AIAgent agent = new SalesPipelineAgenticUI(innerAgent);

app.MapAGUI("/agents/sales-pipeline", agent);

app.Run();
