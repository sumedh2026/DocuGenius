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

// ─── Services ─────────────────────────────────────────────────────────────────

builder.Services.AddSingleton<IJiraService, JiraService>();
builder.Services.AddSingleton<IGitService, GitService>();
builder.Services.AddSingleton<IGroqService, GroqService>();
builder.Services.AddSingleton<IPdfService, PdfService>();

// ─── API + Swagger ────────────────────────────────────────────────────────────

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "DocuGenious API",
        Version = "v1",
        Description = "AI-Powered Documentation Generator API"
    });
});

var app = builder.Build();

// ─── Middleware ───────────────────────────────────────────────────────────────

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "DocuGenious API v1"));
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
