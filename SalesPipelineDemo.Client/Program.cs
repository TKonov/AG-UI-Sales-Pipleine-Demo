using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SalesPipelineDemo.Client;
using SalesPipelineDemo.Client.Services;
using Telerik.Blazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// API base URL — can be overridden in wwwroot/appsettings.json
var apiBase = builder.Configuration["ApiBase"] ?? "https://localhost:7001";

// HttpClient pointed at the API for AG-UI calls
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });

// AG-UI streaming client
builder.Services.AddScoped<AguiClient>();

// Telerik Blazor
builder.Services.AddTelerikBlazor();

await builder.Build().RunAsync();
