# Docu-Genius — Architecture Document

> **Version**: 1.0  
> **Last updated**: April 2026  
> **Target framework**: .NET 10

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Solution Structure](#2-solution-structure)
3. [Layered Architecture](#3-layered-architecture)
4. [Project Descriptions](#4-project-descriptions)
5. [Core Domain Models](#5-core-domain-models)
6. [Interfaces (Contracts)](#6-interfaces-contracts)
7. [API Layer](#7-api-layer)
8. [Blazor Frontend](#8-blazor-frontend)
9. [Integration Services](#9-integration-services)
10. [Configuration System](#10-configuration-system)
11. [Dependency Injection](#11-dependency-injection)
12. [External Dependencies](#12-external-dependencies)
13. [Security Considerations](#13-security-considerations)
14. [Deployment Topology](#14-deployment-topology)

---

## 1. System Overview

Docu-Genius is an **AI-powered documentation generator** that fetches data from JIRA and/or a Git repository, analyses it using the Groq LLM (llama-3.1-8b-instant), and produces a formatted PDF document.

```
┌────────────────────────────────────────────────────────────────┐
│                        User (Browser)                          │
└───────────────────────────┬────────────────────────────────────┘
                            │  HTTP / Blazor WebAssembly
┌───────────────────────────▼────────────────────────────────────┐
│                   Blazor WASM Frontend                         │
│          Home.razor · ValidationService · ApiService           │
└───────────────────────────┬────────────────────────────────────┘
                            │  REST API (JSON over HTTPS)
┌───────────────────────────▼────────────────────────────────────┐
│                    ASP.NET Core Web API                        │
│   DocumentationController · JiraController · GitController     │
│   GroqController           · JobStatusService                  │
└────┬──────────┬────────────┬──────────────────┬───────────────┘
     │          │            │                  │
     ▼          ▼            ▼                  ▼
┌─────────┐ ┌──────┐ ┌────────────┐ ┌──────────────────┐
│  JIRA   │ │ Git  │ │   Groq AI  │ │   PDF Generator  │
│ Service │ │Svc   │ │  Service   │ │    (QuestPDF)    │
└────┬────┘ └──┬───┘ └─────┬──────┘ └────────┬─────────┘
     │         │            │                  │
     ▼         ▼            ▼                  ▼
 Atlassian  LibGit2   Groq REST API       ./output/*.pdf
   JIRA      Sharp    (OpenAI-compatible)
```

---

## 2. Solution Structure

```
DocuGenious/
├── src/
│   ├── DocuGenious.Core/               # Domain layer — no external dependencies
│   ├── DocuGenious.Integration.Jira/   # JIRA integration (Atlassian SDK)
│   ├── DocuGenious.Integration.Git/    # Git integration (LibGit2Sharp)
│   ├── DocuGenious.Integration.Groq/   # AI analysis (OpenAI SDK → Groq)
│   ├── DocuGenious.Integration.Pdf/    # PDF generation (QuestPDF)
│   ├── DocuGenious.Api/                # ASP.NET Core Web API host
│   ├── DocuGenious.Blazor/             # Blazor WebAssembly frontend
│   └── DocuGenious.Console/            # CLI alternative (Spectre.Console)
└── docs/
    ├── Architecture.md                 # This document
    └── FunctionalFlow.md               # Functional flow document
```

---

## 3. Layered Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  Presentation Layer                                          │
│  DocuGenious.Blazor  ·  DocuGenious.Console                  │
├──────────────────────────────────────────────────────────────┤
│  API Layer                                                   │
│  DocuGenious.Api  (Controllers, CORS, Swagger, DI wiring)    │
├──────────────────────────────────────────────────────────────┤
│  Integration Layer                                           │
│  Integration.Jira  ·  Integration.Git                        │
│  Integration.Groq  ·  Integration.Pdf                        │
├──────────────────────────────────────────────────────────────┤
│  Domain / Core Layer                                         │
│  DocuGenious.Core  (Interfaces · Models · Configuration)     │
└──────────────────────────────────────────────────────────────┘
```

**Dependency rule**: every layer depends only on layers below it. The Core layer has **zero** external package dependencies — it defines contracts that the Integration layer implements.

---

## 4. Project Descriptions

### 4.1 `DocuGenious.Core`
The **domain layer**. Contains:
- All interface contracts (`IJiraService`, `IGitService`, `IGroqService`, `IPdfService`)
- All shared data models (`DocumentationRequest`, `AnalysisResult`, `JiraTicket`, `GitRepositoryInfo`, etc.)
- Configuration POCOs (`AppSettings` and its nested classes)
- Enumerations (`DocumentationType`, `SourceType`)

**No NuGet dependencies.** All other projects reference this.

### 4.2 `DocuGenious.Integration.Jira`
Implements `IJiraService` using the **Atlassian.SDK** (v13.0.0).

Key responsibilities:
- Fetch JIRA tickets by key using `Atlassian.Jira.Jira.CreateRestClient`
- Fetch comments via `issue.GetCommentsAsync()`
- Fetch sub-tasks via direct `HttpClient` call to `/rest/api/3/search/jql` (the Atlassian SDK targets the deprecated v2 search endpoint)
- Parse acceptance criteria from ticket descriptions
- Translate raw SDK error JSON (`errorMessages` array) into plain English
- Validate JIRA connection

### 4.3 `DocuGenious.Integration.Git`
Implements `IGitService` using **LibGit2Sharp** (v0.30.0).

Key responsibilities:
- Clone remote repositories to `Git:CloneDirectory`
- Analyse local or cloned repositories: commits, branches, contributors, file content
- Detect technology stack from file extensions
- Build a 2-level directory tree
- Validate remote repository accessibility (GitHub REST API for GitHub URLs, LibGit2Sharp for others)
- Validate branch existence

### 4.4 `DocuGenious.Integration.Groq`
Implements `IGroqService` using the **OpenAI .NET SDK** (v2.1.0) pointed at Groq's OpenAI-compatible endpoint.

Key responsibilities:
- Build per-doc-type system prompts, focus instructions, and compact JSON schema hints
- Truncate JIRA/Git context to fit within Groq's 6,000 TPM free-tier limit
- Stream the LLM response (`CompleteChatStreamingAsync`)
- Parse the response through up to 8 JSON extraction strategies (raw, fenced, brace-extracted, each with and without literal-newline repair)
- Score output quality and log warnings
- Auto back-off on 429 rate-limit responses (reads `"try again in Xs"` from Groq's error body)

**Groq free-tier constraints handled**:
| Limit | Value | Mitigation |
|---|---|---|
| Tokens/minute (TPM) | 6,000 | Input trimmed to ~500 tokens; `MaxTokens` = 4,000; auto back-off on 429 |
| Requests/minute (RPM) | 30 | Well within single-user usage |
| Network timeout | SDK default | `NetworkTimeout = 10 min`; per-call `CancellationTokenSource(300 s)` |

### 4.5 `DocuGenious.Integration.Pdf`
Implements `IPdfService` using **QuestPDF** (v2024.10.2, Community licence).

Key responsibilities:
- Render an A4 PDF from an `AnalysisResult`
- Parse markdown in string fields (headings `##`, bullets `-/*`, numbered lists, bold `**`, italic `*`, code `` ` ``, fenced code blocks, blockquotes)
- Conditionally include sections based on which fields are populated (e.g. API Endpoints section only if `ApiEndpoints.Count > 0`)
- Save the file to `Output:PdfDirectory` (default `./output`)

### 4.6 `DocuGenious.Api`
The **ASP.NET Core Web API** host. Runs as a standalone HTTPS server.

Responsibilities:
- Expose REST endpoints for documentation generation, JIRA/Git validation, and Groq analysis
- Track in-progress job status via `JobStatusService` (in-memory `ConcurrentDictionary`)
- Serve generated PDF files for download
- CORS policy for Blazor WASM origin and DevTunnel origins
- Swagger UI (Development only)

### 4.7 `DocuGenious.Blazor`
The **Blazor WebAssembly** single-page application. Runs entirely in the browser.

Responsibilities:
- Present a form for source selection (JIRA IDs, Git URL), document type, and output options
- Run client-side and server-side validation with live per-row UI feedback
- Orchestrate the generate → poll status → success/error flow
- Persist generated file history to `wwwroot/data/files.json` (browser-local via JS interop)
- Trigger PDF downloads via `downloadFileFromBase64` JS helper

### 4.8 `DocuGenious.Console`
An interactive **CLI alternative** using **Spectre.Console**. Does not require the API server. Directly instantiates all integration services and runs the same 4-step pipeline in the terminal with progress indicators and a results table.

---

## 5. Core Domain Models

### `DocumentationRequest`
The input to the documentation pipeline.

| Property | Type | Description |
|---|---|---|
| `JobId` | `string?` | Assigned by Blazor; used to track live status |
| `SourceType` | `SourceType` | `JiraOnly`, `GitOnly`, or `Both` |
| `JiraTicketIds` | `List<string>` | One or more JIRA ticket keys |
| `GitRepositoryUrl` | `string?` | Remote Git URL to clone |
| `GitLocalPath` | `string?` | Alternative: path to a local repository |
| `GitBranch` | `string?` | Branch to check out (defaults to repo default) |
| `DocumentationType` | `DocumentationType` | Which style of document to generate |
| `OutputFileName` | `string` | Base name (without extension) for the PDF |
| `AdditionalContext` | `string?` | Free-text context injected into the prompt |
| `MaxFilesToAnalyze` | `int` | Default 50 — limits Git file content loaded |
| `FileExtensionsToInclude` | `string` | Default `.cs,.ts,.js,.py,.java,.go,.rs,.md` |

### `AnalysisResult`
The structured output from the Groq AI analysis; used directly by the PDF renderer.

| Property | Type | Notes |
|---|---|---|
| `ExecutiveSummary` | `string` | Always populated |
| `TechnicalOverview` | `string` | Markdown, populated for Technical/Full/Architecture docs |
| `ArchitectureDescription` | `string` | Markdown |
| `UserGuide` | `string` | Markdown, populated for User Guide / Full docs |
| `SetupInstructions` | `string` | Markdown |
| `ConfigurationGuide` | `string` | Key/value table-style text |
| `Features` | `List<Feature>` | `{ Name, Description, UsageExample }` |
| `ApiEndpoints` | `List<ApiEndpoint>` | `{ Method, Path, Description, RequestBody, ResponseBody }` |
| `Dependencies` | `List<string>` | "Package vX — purpose" format |
| `Recommendations` | `List<string>` | |
| `KnownIssues` | `List<string>` | |
| `DocumentationType` | `DocumentationType` | Set by `GroqService` after parse |
| `SourceInfo` | `string` | E.g. "JIRA: SCRUM-12, SCRUM-13" |
| `GeneratedAt` | `DateTime` | UTC timestamp |

### `DocumentationType` Enum

| Value | Description |
|---|---|
| `TechnicalDocumentation` | Developer-focused; setup, architecture, config, API |
| `UserGuide` | Non-technical; step-by-step instructions |
| `ApiDocumentation` | API reference; all endpoints with request/response |
| `ArchitectureOverview` | Senior engineers; components, data flow, integrations |
| `FullDocumentation` | All sections combined |

### `SourceType` Enum

| Value | Description |
|---|---|
| `JiraOnly` | Source data from JIRA tickets only |
| `GitOnly` | Source data from a Git repository only |
| `Both` | Combined: JIRA requirements + Git implementation |

---

## 6. Interfaces (Contracts)

All defined in `DocuGenious.Core.Interfaces`.

```csharp
// JIRA data access
interface IJiraService
{
    Task<JiraTicket>       GetTicketAsync(string ticketId);
    Task<List<JiraTicket>> GetTicketsAsync(IEnumerable<string> ticketIds);
    Task<bool>             ValidateConnectionAsync();
}

// Git repository analysis
interface IGitService
{
    Task<GitRepositoryInfo>    AnalyzeLocalRepositoryAsync(string localPath, string? branch, DocumentationRequest? request);
    Task<GitRepositoryInfo>    CloneAndAnalyzeAsync(string repositoryUrl, string? branch, DocumentationRequest? request);
    Task<bool>                 ValidateLocalRepositoryAsync(string localPath);
    Task<RepoValidationResult> ValidateRemoteRepositoryAsync(string repositoryUrl, string? branch);
}

// AI analysis (LLM)
interface IGroqService
{
    Task<AnalysisResult> AnalyzeJiraTicketsAsync(List<JiraTicket> tickets, DocumentationType docType, string? additionalContext);
    Task<AnalysisResult> AnalyzeGitRepositoryAsync(GitRepositoryInfo repoInfo, DocumentationType docType, string? additionalContext);
    Task<AnalysisResult> AnalyzeCombinedAsync(List<JiraTicket> tickets, GitRepositoryInfo repoInfo, DocumentationType docType, string? additionalContext);
    Task<bool>           ValidateConnectionAsync();
}

// PDF rendering
interface IPdfService
{
    Task<string> GeneratePdfAsync(AnalysisResult result, string outputFileName);
    // Returns the absolute path of the saved PDF file.
}
```

---

## 7. API Layer

### Controllers and Endpoints

#### `DocumentationController` — `/api/documentation`

| Method | Endpoint | Purpose |
|---|---|---|
| `POST` | `/generate` | All-in-one pipeline: fetch → analyse → PDF → return file name |
| `GET` | `/status/{jobId}` | Poll live progress (200 + `{ status }` or 204 when done) |
| `GET` | `/download/{fileName}` | Download a previously generated PDF |

#### `JiraController` — `/api/jira`

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/validate` | Test JIRA credentials and connectivity |
| `GET` | `/ticket/{ticketId}` | Fetch a single ticket |
| `POST` | `/tickets` | Fetch multiple tickets by ID list |
| `POST` | `/validate-tickets` | Existence + status check (used by Blazor validation) |

#### `GitController` — `/api/git`

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/validate-remote?url=&branch=` | Validate remote URL and optional branch |
| `POST` | `/analyse/local` | Analyse a local repository path |
| `POST` | `/analyse/remote` | Clone and analyse a remote repository |

#### `GroqController` — `/api/groq`

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/validate` | Test Groq API key |
| `POST` | `/analyse/jira` | Run AI analysis on a JIRA ticket list |
| `POST` | `/analyse/git` | Run AI analysis on a `GitRepositoryInfo` |
| `POST` | `/analyse/combined` | Run AI analysis on JIRA + Git combined |

### `JobStatusService`
An in-memory singleton that maps `jobId → (status, expiresAt)` using `ConcurrentDictionary<string, Entry>`. Entries expire after 30 minutes. The controller writes status strings at key milestones; the Blazor frontend polls every 1.5 s to display them.

---

## 8. Blazor Frontend

### Component Hierarchy

```
App.razor
└── MainLayout.razor  (.dg-page gradient wrapper)
    └── Home.razor    (entire UI — single page)
```

### Services (registered in `Program.cs`)

| Service | Lifetime | Responsibility |
|---|---|---|
| `DocumentationApiService` | Scoped | HTTP client wrapper for all API calls |
| `ValidationService` | Scoped | Client-side + server-side pre-flight validation |
| `FileStorageService` | Singleton | Persists file history to `wwwroot/data/files.json` |

### UI State Machine

```
Form ──[Generate clicked]──► Validating
                                 │
                    ┌────────────┴────────────┐
                    │ canProceed = true        │ canProceed = false
                    ▼                          ▼
                Generating               (stay in Validating,
                    │                     show Close button)
           ┌────────┴────────┐
           │ success          │ error
           ▼                  ▼
        Success             Error
           │                  │
         Close              Close
           ▼                  ▼
          Form              Form
         (reset)          (fields kept)
```

### Configuration

`wwwroot/appsettings.json`:
```json
{ "ApiBaseUrl": "https://localhost:60735/" }
```

Override for DevTunnel in `wwwroot/appsettings.DevTunnel.json` (gitignored):
```json
{ "ApiBaseUrl": "https://<tunnel-id>.devtunnels.ms/" }
```

---

## 9. Integration Services

### 9.1 Groq (LLM) — Prompt Architecture

Each documentation call builds a 3-part prompt:

```
System Prompt  (~60 tokens)
  └─ Role + 4 rules: output-only JSON, use source data, no tool branding

User Prompt
  ├─ Focus instructions  (~25 tokens per doc type)
  ├─ Additional context  (optional, user-supplied)
  ├─ Source data block   (JIRA/Git context, truncated to stay under TPM limit)
  └─ JSON schema hint    (~150 tokens — compact single-line template)
```

**JSON repair pipeline** (handles `llama-3.1-8b-instant` quirks):
1. Raw response as-is
2. Raw response with literal `\n`/`\r`/`\t` inside strings escaped (`FixLiteralNewlinesInStrings`)
3. Content extracted from ` ```json ``` ` fence
4. Same, repaired
5. Content extracted from generic ` ``` ``` ` fence
6. Same, repaired
7. Brace-extracted (first `{` to matching `}`, skipping strings)
8. Same, repaired

The first candidate that deserialises into a non-empty `AnalysisResult` wins.

### 9.2 JIRA — Authentication

Uses **HTTP Basic Auth** with the JIRA cloud username and an API token (not a password). Two clients are created:
- **Atlassian SDK client** for ticket fetch, comments, project listing
- **Raw `HttpClient`** for sub-task fetch via `/rest/api/3/search/jql` (the SDK targets the removed v2 endpoint)

### 9.3 Git — Repository Analysis

| Operation | Library | Notes |
|---|---|---|
| Clone | LibGit2Sharp | Clones to `Git:CloneDirectory/{repoName}` |
| Commit history | LibGit2Sharp | Last 30 commits |
| File content | `System.IO.File` | Files ≤ 100 KB, up to 50 files |
| Technology detection | Extension mapping | `.cs`→C#, `.ts`→TypeScript, etc. |
| GitHub validation | GitHub REST API | `GET /repos/{owner}/{repo}` with PAT |
| Other Git validation | LibGit2Sharp | `Repository.ListRemoteReferences` |

---

## 10. Configuration System

All configuration is loaded via .NET's standard `IConfiguration` and bound to strongly-typed `AppSettings` POCOs.

```csharp
// AppSettings hierarchy
AppSettings
  ├── JiraSettings    { BaseUrl, Username, ApiToken }
  ├── GitSettings     { Username, PersonalAccessToken, CloneDirectory }
  ├── GroqSettings    { ApiKey, Model, MaxTokens, BaseUrl, TimeoutSeconds }
  └── OutputSettings  { PdfDirectory }
```

### Environment-specific files (API)

| File | Purpose | In Git? |
|---|---|---|
| `appsettings.json` | Production defaults (placeholder values) | ✅ Yes |
| `appsettings.Development.json` | Real dev credentials | ❌ No (gitignored) |
| `appsettings.DevTunnel.json` | Tunnel CORS origin override | ❌ No (gitignored) |

---

## 11. Dependency Injection

### API `Program.cs`

```
AppSettings           Singleton  ← IConfiguration binding
JobStatusService      Singleton  ← in-memory ConcurrentDictionary
IJiraService          Singleton  → JiraService
IGitService           Singleton  → GitService
IGroqService          Singleton  → GroqService
IPdfService           Singleton  → PdfService
```

All integration services are **singletons** because they hold pre-configured clients (Atlassian SDK, OpenAI SDK, HttpClient) and are stateless with respect to individual requests.

### Blazor `Program.cs`

```
HttpClient                Scoped    BaseAddress=ApiBaseUrl, Timeout=3 min
DocumentationApiService   Scoped
ValidationService         Scoped
FileStorageService        Singleton
```

---

## 12. External Dependencies

| Package | Version | Project | Purpose |
|---|---|---|---|
| `Atlassian.SDK` | 13.0.0 | Integration.Jira | JIRA REST client; Basic Auth with API token |
| `LibGit2Sharp` | 0.30.0 | Integration.Git | Git clone, history, and file analysis |
| `OpenAI` | 2.1.0 | Integration.Groq | OpenAI-compatible SDK; pointed at Groq's endpoint |
| `QuestPDF` | 2024.10.2 | Integration.Pdf | Fluent PDF generation (Community licence) |
| `Swashbuckle.AspNetCore` | 7.3.1 | Api | Swagger/OpenAPI documentation UI |
| `Microsoft.AspNetCore.Components.WebAssembly` | 10.0.0 | Blazor | Blazor WASM runtime and routing |
| `Spectre.Console` | latest | Console | Rich terminal UI (progress bars, tables, prompts) |

All projects target **net10.0**.

---

## 13. Security Considerations

| Concern | Mitigation |
|---|---|
| Path traversal in PDF download | `Path.GetFileName(fileName)` strips any directory components before building the full path |
| Credential storage | JIRA API token, Git PAT, and Groq API key are stored in `appsettings.Development.json` (gitignored); production deployments should use environment variables or a secrets manager |
| CORS | Explicit allowlist in `Cors:AllowedOrigins`; DevTunnel domain added only in development |
| JIRA API token | Scoped to read-only JIRA operations; never logged |
| Token injection in PDF | Prompt explicitly instructs the LLM to use only SOURCE DATA; `HasMeaningfulContent` check rejects empty/hallucinated output |
| Free-tier rate limits | Auto back-off on 429 prevents user-facing failures from transient throttling |

---

## 14. Deployment Topology

### Development (local)

```
Browser
  └── http://localhost:5173  (Blazor WASM dev server / hot reload)
        └── https://localhost:60735  (API dev server)
              ├── Atlassian JIRA Cloud  (external)
              ├── GitHub.com            (external, for Git clone)
              └── api.groq.com          (external, LLM)
```

### Development with DevTunnel (remote access)

```
Browser (any device)
  └── https://<id>.devtunnels.ms  (Blazor via tunnel)
        └── https://<id>.devtunnels.ms  (API via same or different tunnel)
```

### Production (recommended)

```
CDN / Static host  (Blazor WASM — static files only)
  └── Load Balancer / Reverse Proxy
        └── Docker container  (API + PDF output volume)
              ├── External: JIRA Cloud
              ├── External: Git provider
              └── External: Groq API
```

Output PDFs are written to `./output` relative to the API working directory. In production, this should be a mounted volume or replaced with blob storage.
