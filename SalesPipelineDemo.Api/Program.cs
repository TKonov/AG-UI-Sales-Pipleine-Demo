using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.SignalR;
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
// The agent now takes IHubContext<SalesQualityHub> so bulk_operation can
// broadcast SignalR previews internally — Option C hybrid approach.
var store          = app.Services.GetRequiredService<OpportunityStore>();
var contractSvc    = app.Services.GetRequiredService<MockContractService>();
var hubContext     = app.Services.GetRequiredService<IHubContext<SalesQualityHub>>();
var agentLogger    = app.Services.GetRequiredService<ILogger<SalesPipelineChatClient>>();
var config         = app.Services.GetRequiredService<IConfiguration>();

ChatClientAgent agent = new ChatClientAgent(
    new SalesPipelineChatClient(store, contractSvc, hubContext, agentLogger, config),
    instructions: null,
    name: "SalesPipelineAgent");

app.MapAGUI("/agents/sales-pipeline", agent);

app.Run();
