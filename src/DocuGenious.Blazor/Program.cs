using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using DocuGenious.Blazor;
using DocuGenious.Blazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Load appsettings.DevTunnel.json if present — overrides ApiBaseUrl for Dev Tunnel sessions.
// Copy appsettings.DevTunnel.json.example → appsettings.DevTunnel.json and fill in your tunnel URL.
// This file is gitignored and will never be committed.
var baseHttp = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
try
{
    var response = await baseHttp.GetAsync("appsettings.DevTunnel.json");
    if (response.IsSuccessStatusCode)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        builder.Configuration.AddJsonStream(stream);
    }
}
catch { /* DevTunnel config not present — using appsettings.json */ }

var apiBase = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:60735/";

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });
builder.Services.AddScoped<DocumentationApiService>();
builder.Services.AddScoped<ValidationService>();
builder.Services.AddSingleton<FileStorageService>();
await builder.Build().RunAsync();
