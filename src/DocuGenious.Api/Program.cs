using DocuGenious.Core.Configuration;
using DocuGenious.Core.Interfaces;
using DocuGenious.Integration.Git;
using DocuGenious.Integration.Groq;
using DocuGenious.Integration.Jira;
using DocuGenious.Integration.Pdf;

var builder = WebApplication.CreateBuilder(args);

// ─── Configuration ────────────────────────────────────────────────────────────

var appSettings = new AppSettings();
builder.Configuration.Bind(appSettings);
builder.Services.AddSingleton(appSettings);

// ─── CORS (allow Blazor WASM dev origin + any configured origins) ─────────────

builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorPolicy", policy =>
    {
        policy
            .WithOrigins(
                "https://localhost:7170",   // Blazor WASM https
                "http://localhost:5138",    // Blazor WASM http
                "https://localhost:7002",
                "http://localhost:5002",
                "https://localhost:7001",
                "http://localhost:5001")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

// ─── Services ─────────────────────────────────────────────────────────────────

builder.Services.AddSingleton<IJiraService, JiraService>();
builder.Services.AddSingleton<IGitService, GitService>();
builder.Services.AddSingleton<IGroqService, GroqService>();
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
        Description = "AI-Powered Documentation Generator API — backed by Groq / LLaMA"
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
