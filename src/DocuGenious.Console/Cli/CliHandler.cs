using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using Spectre.Console;

namespace DocuGenious.Console.Cli;

/// <summary>
/// Drives the interactive command-line experience using Spectre.Console.
/// </summary>
public class CliHandler
{
    private readonly IJiraService _jiraService;
    private readonly IGitService _gitService;
    private readonly IGroqService _groqService;
    private readonly IPdfService _pdfService;

    public CliHandler(
        IJiraService jiraService,
        IGitService gitService,
        IGroqService groqService,
        IPdfService pdfService)
    {
        _jiraService = jiraService;
        _gitService = gitService;
        _groqService = groqService;
        _pdfService = pdfService;
    }

    public async Task RunAsync()
    {
        PrintBanner();

        await ValidateServicesAsync();

        while (true)
        {
            var request = await CollectRequestAsync();
            if (request == null) break;

            await ProcessRequestAsync(request);

            if (!AnsiConsole.Confirm("\n[blue]Generate another document?[/]", defaultValue: false))
                break;

            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine("\n[grey]Thank you for using Docu-Genius. Goodbye![/]");
    }

    // ─── Banner ──────────────────────────────────────────────────────────────────

    private static void PrintBanner()
    {
        AnsiConsole.Write(
            new FigletText("Docu-Genius")
                .Centered()
                .Color(Color.SteelBlue1));

        AnsiConsole.Write(
            new Rule("[steelblue1]AI-Powered Documentation Generator[/]")
                .Centered());

        AnsiConsole.WriteLine();
    }

    // ─── Service validation ──────────────────────────────────────────────────────

    private async Task ValidateServicesAsync()
    {
        AnsiConsole.MarkupLine("[grey]Validating service connections...[/]");

        await AnsiConsole.Status()
            .StartAsync("Connecting to Gemini AI...", async ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                try
                {
                    var ok = await _groqService.ValidateConnectionAsync();
                    AnsiConsole.MarkupLine(ok
                        ? "  [green]✓[/] Gemini AI connection OK"
                        : "  [yellow]⚠[/] Gemini AI connection could not be verified — check your API key");
                }
                catch (InvalidOperationException ex)
                {
                    AnsiConsole.MarkupLine($"  [red]✗[/] {ex.Message}");
                    AnsiConsole.WriteLine();
                    Environment.Exit(1);
                }
            });

        AnsiConsole.WriteLine();
    }

    // ─── Request collection ──────────────────────────────────────────────────────

    private async Task<DocumentationRequest?> CollectRequestAsync()
    {
        // 1. Source type
        var sourceChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[steelblue1]Select your input source:[/]")
                .AddChoices(
                    "JIRA Ticket(s) only",
                    "Git Repository only",
                    "Both JIRA + Git Repository",
                    "[grey]Exit[/]"));

        if (sourceChoice.Contains("Exit", StringComparison.OrdinalIgnoreCase))
            return null;

        var request = new DocumentationRequest();

        if (sourceChoice.Contains("JIRA") && !sourceChoice.Contains("Git"))
            request.SourceType = SourceType.JiraOnly;
        else if (sourceChoice.Contains("Git") && !sourceChoice.Contains("JIRA"))
            request.SourceType = SourceType.GitOnly;
        else
            request.SourceType = SourceType.Both;

        // 2. JIRA details
        if (request.SourceType is SourceType.JiraOnly or SourceType.Both)
        {
            var ticketInput = AnsiConsole.Prompt(
                new TextPrompt<string>("[steelblue1]Enter JIRA ticket ID(s)[/] [grey](comma-separated, e.g. PROJ-123, PROJ-124)[/]:")
                    .Validate(v =>
                        v.Split(',').Any(x => !string.IsNullOrWhiteSpace(x))
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Please enter at least one ticket ID")));

            request.JiraTicketIds = ticketInput
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        // 3. Git details
        if (request.SourceType is SourceType.GitOnly or SourceType.Both)
        {
            var gitMode = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[steelblue1]Git repository source:[/]")
                    .AddChoices("Remote URL (clone)", "Local path"));

            if (gitMode.StartsWith("Remote"))
            {
                request.GitRepositoryUrl = AnsiConsole.Prompt(
                    new TextPrompt<string>("[steelblue1]Repository URL:[/]")
                        .Validate(v => !string.IsNullOrWhiteSpace(v)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("URL cannot be empty")));
            }
            else
            {
                request.GitLocalPath = AnsiConsole.Prompt(
                    new TextPrompt<string>("[steelblue1]Local repository path:[/]")
                        .Validate(v => !string.IsNullOrWhiteSpace(v)
                            ? ValidationResult.Success()
                            : ValidationResult.Error("Path cannot be empty")));
            }

            var branchInput = AnsiConsole.Prompt(
                new TextPrompt<string>("[steelblue1]Branch[/] [grey](leave blank for default)[/]:")
                    .AllowEmpty());

            if (!string.IsNullOrWhiteSpace(branchInput))
                request.GitBranch = branchInput;
        }

        // 4. Documentation type
        request.DocumentationType = AnsiConsole.Prompt(
            new SelectionPrompt<DocumentationType>()
                .Title("[steelblue1]Select documentation type:[/]")
                .UseConverter(dt => dt switch
                {
                    DocumentationType.FullDocumentation => "Full Documentation (comprehensive)",
                    DocumentationType.UserGuide => "User Guide (end-user focused)",
                    DocumentationType.TechnicalDocumentation => "Technical Documentation",
                    DocumentationType.ApiDocumentation => "API Documentation",
                    DocumentationType.ArchitectureOverview => "Architecture Overview",
                    _ => dt.ToString()
                })
                .AddChoices(
                    DocumentationType.FullDocumentation,
                    DocumentationType.UserGuide,
                    DocumentationType.TechnicalDocumentation,
                    DocumentationType.ApiDocumentation,
                    DocumentationType.ArchitectureOverview));

        // 5. Advanced options
        if (AnsiConsole.Confirm("[grey]Configure advanced options?[/]", defaultValue: false))
        {
            request.IncludeCodeSnippets = AnsiConsole.Confirm("Include code snippets in analysis?", defaultValue: true);

            request.MaxFilesToAnalyze = AnsiConsole.Prompt(
                new TextPrompt<int>("[steelblue1]Max files to analyse from repo[/] [grey](default 50)[/]:")
                    .DefaultValue(50)
                    .Validate(v => v > 0 && v <= 500
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Must be between 1 and 500")));
        }

        // 6. Output filename
        var defaultName = BuildDefaultFileName(request);
        request.OutputFileName = AnsiConsole.Prompt(
            new TextPrompt<string>($"[steelblue1]Output file name[/] [grey](default: {defaultName})[/]:")
                .DefaultValue(defaultName)
                .AllowEmpty()) ?? defaultName;

        if (string.IsNullOrWhiteSpace(request.OutputFileName))
            request.OutputFileName = defaultName;

        // Confirm
        AnsiConsole.WriteLine();
        RenderRequestSummary(request);
        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("[steelblue1]Proceed with generation?[/]"))
            return await CollectRequestAsync();

        return request;
    }

    private static void RenderRequestSummary(DocumentationRequest request)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.SteelBlue1)
            .AddColumn("[grey]Setting[/]")
            .AddColumn("[grey]Value[/]");

        table.AddRow("Source", request.SourceType.ToString());

        if (request.JiraTicketIds.Count > 0)
            table.AddRow("JIRA Tickets", string.Join(", ", request.JiraTicketIds));

        if (!string.IsNullOrWhiteSpace(request.GitRepositoryUrl))
            table.AddRow("Git URL", request.GitRepositoryUrl);

        if (!string.IsNullOrWhiteSpace(request.GitLocalPath))
            table.AddRow("Git Path", request.GitLocalPath);

        if (!string.IsNullOrWhiteSpace(request.GitBranch))
            table.AddRow("Branch", request.GitBranch);

        table.AddRow("Doc Type", request.DocumentationType.ToString());
        table.AddRow("Output", $"{request.OutputFileName}.pdf");

        AnsiConsole.Write(table);
    }

    // ─── Processing ──────────────────────────────────────────────────────────────

    private async Task ProcessRequestAsync(DocumentationRequest request)
    {
        List<JiraTicket>? tickets = null;
        GitRepositoryInfo? repoInfo = null;
        AnalysisResult? analysisResult = null;

        try
        {
            await AnsiConsole.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new SpinnerColumn(Spinner.Known.Dots))
                .StartAsync(async ctx =>
                {
                    // Step 1 — Fetch JIRA
                    if (request.SourceType is SourceType.JiraOnly or SourceType.Both)
                    {
                        var jiraTask = ctx.AddTask("[blue]Fetching JIRA ticket(s)[/]", maxValue: request.JiraTicketIds.Count);

                        tickets = [];
                        foreach (var id in request.JiraTicketIds)
                        {
                            tickets.Add(await _jiraService.GetTicketAsync(id));
                            jiraTask.Increment(1);
                        }
                        jiraTask.Value = jiraTask.MaxValue;
                    }

                    // Step 2 — Fetch Git
                    if (request.SourceType is SourceType.GitOnly or SourceType.Both)
                    {
                        var gitTask = ctx.AddTask("[blue]Analysing repository[/]", maxValue: 1);

                        repoInfo = !string.IsNullOrWhiteSpace(request.GitRepositoryUrl)
                            ? await _gitService.CloneAndAnalyzeAsync(request.GitRepositoryUrl, request.GitBranch, request)
                            : await _gitService.AnalyzeLocalRepositoryAsync(request.GitLocalPath!, request.GitBranch, request);

                        gitTask.Value = 1;
                    }

                    // Step 3 — Gemini AI analysis
                    var aiTask = ctx.AddTask("[blue]Analysing with Gemini AI[/]", maxValue: 1);

                    analysisResult = request.SourceType switch
                    {
                        SourceType.JiraOnly => await _groqService.AnalyzeJiraTicketsAsync(tickets!, request.DocumentationType),
                        SourceType.GitOnly => await _groqService.AnalyzeGitRepositoryAsync(repoInfo!, request.DocumentationType),
                        _ => await _groqService.AnalyzeCombinedAsync(tickets!, repoInfo!, request.DocumentationType)
                    };

                    aiTask.Value = 1;

                    // Step 4 — PDF generation
                    var pdfTask = ctx.AddTask("[blue]Generating PDF[/]", maxValue: 1);
                    await _pdfService.GeneratePdfAsync(analysisResult, request.OutputFileName);
                    pdfTask.Value = 1;
                });

            AnsiConsole.Write(
                new Panel($"[green]✓ Documentation generated successfully![/]\n[grey]File:[/] [white]{request.OutputFileName}.pdf[/]")
                    .BorderColor(Color.Green)
                    .Padding(1, 0));
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static string BuildDefaultFileName(DocumentationRequest request)
    {
        var parts = new List<string>();

        if (request.JiraTicketIds.Count > 0)
            parts.Add(request.JiraTicketIds[0]);

        if (!string.IsNullOrWhiteSpace(request.GitRepositoryUrl))
        {
            var repoName = request.GitRepositoryUrl.TrimEnd('/').Split('/').LastOrDefault() ?? "repo";
            parts.Add(repoName.Replace(".git", ""));
        }

        parts.Add(request.DocumentationType.ToString());
        parts.Add(DateTime.UtcNow.ToString("yyyyMMdd"));

        return string.Join("_", parts);
    }
}
