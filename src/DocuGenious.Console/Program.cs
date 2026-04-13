using DocuGenious.Console.Cli;
using DocuGenious.Core.Configuration;
using DocuGenious.Core.Interfaces;
using DocuGenious.Integration.Git;
using DocuGenious.Integration.Groq;
using DocuGenious.Integration.Jira;
using DocuGenious.Integration.Pdf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

// ─── Build configuration ──────────────────────────────────────────────────────

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables()      // Environment variables override appsettings.json
    .Build();

var settings = new AppSettings();
configuration.Bind(settings);

// ─── Build DI container ───────────────────────────────────────────────────────

var services = new ServiceCollection();

// Logging
services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Warning); // Suppress verbose SDK logs in CLI
});

// Settings (singleton)
services.AddSingleton(settings);

// Register services
services.AddSingleton<IJiraService, JiraService>();
services.AddSingleton<IGitService, GitService>();
services.AddSingleton<IGroqService, GroqService>();
services.AddSingleton<IPdfService, PdfService>();
services.AddSingleton<CliHandler>();

var serviceProvider = services.BuildServiceProvider();

// ─── Run ──────────────────────────────────────────────────────────────────────

try
{
    var cli = serviceProvider.GetRequiredService<CliHandler>();
    await cli.RunAsync();
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
    AnsiConsole.MarkupLine("\n[red]Fatal error. Please check your configuration in appsettings.json.[/]");
    Environment.Exit(1);
}
