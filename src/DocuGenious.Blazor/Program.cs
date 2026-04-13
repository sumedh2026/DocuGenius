using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using DocuGenious.Blazor;
using DocuGenious.Blazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Read API base URL from appsettings — defaults to the API project's local URL
var apiBase = builder.Configuration["ApiBaseUrl"] ?? "https://localhost:7001/";

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });
builder.Services.AddScoped<DocumentationApiService>();

await builder.Build().RunAsync();
