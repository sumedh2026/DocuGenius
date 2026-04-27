using DocuGenious.Api.Services;
using DocuGenious.Core.Configuration;
using DocuGenious.Core.Interfaces;
using DocuGenious.Integration.Gemini;
using DocuGenious.Integration.Git;
using DocuGenious.Integration.Jira;
using DocuGenious.Integration.Pdf;

var builder = WebApplication.CreateBuilder(args);

// ─── Configuration ────────────────────────────────────────────────────────────

var appSettings = new AppSettings();
builder.Configuration.Bind(appSettings);
builder.Services.AddSingleton(appSettings);

// ─── CORS ─────────────────────────────────────────────────────────────────────

// Static localhost origins always allowed (dev machine)
var localhostOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "https://localhost:7170",
    "http://localhost:5138",
    "https://localhost:7002",
    "http://localhost:5002",
    "https://localhost:7001",
    "http://localhost:5001"
};

// Additional origins from appsettings (e.g. production / staging URLs)
var configOrigins = (builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [])
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorPolicy", policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                // 1. Always allow localhost dev origins
                if (localhostOrigins.Contains(origin)) return true;

                // 2. Allow any origin configured in appsettings Cors:AllowedOrigins
                if (configOrigins.Contains(origin)) return true;

                // 3. Allow any *.devtunnels.ms origin in Development (QA via Dev Tunnel)
                if (builder.Environment.IsDevelopment() &&
                    Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                    uri.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase))
                    return true;

                return false;
            })
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// ─── Services ─────────────────────────────────────────────────────────────────

builder.Services.AddSingleton<JobStatusService>();
builder.Services.AddSingleton<IJiraService, JiraService>();
builder.Services.AddSingleton<IGitService, GitService>();
builder.Services.AddSingleton<IGroqService, GeminiService>();
builder.Services.AddSingleton<IPdfService, PdfService>();

// ─── API + Swagger ────────────────────────────────────────────────────────────

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title       = "Docu-Genius API",
        Version     = "v1",
        Description = "AI-Powered Documentation Generator API — backed by Google Gemini"
    });
});

var app = builder.Build();

// ─── Middleware ───────────────────────────────────────────────────────────────

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Docu-Genius API v1"));
}

app.UseCors("BlazorPolicy");
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
