# Docu-Genius — Functional Flow Document

> **Version**: 1.0  
> **Last updated**: April 2026  
> **Target framework**: .NET 10

---

## Table of Contents

1. [Overview](#1-overview)
2. [Complete Generate-Document Flow](#2-complete-generate-document-flow)
3. [Validation Flow](#3-validation-flow)
4. [Document Generation Flow](#4-document-generation-flow)
5. [AI Prompt & JSON Repair Flow](#5-ai-prompt--json-repair-flow)
6. [Job Status Polling Flow](#6-job-status-polling-flow)
7. [PDF Generation Flow](#7-pdf-generation-flow)
8. [File Download Flow](#8-file-download-flow)
9. [File History Persistence Flow](#9-file-history-persistence-flow)
10. [Error Handling Flows](#10-error-handling-flows)
11. [UI State Machine](#11-ui-state-machine)

---

## 1. Overview

Docu-Genius transforms raw JIRA tickets and/or a Git repository into a polished PDF document.  
The end-to-end journey passes through four major stages:

```
  ┌──────────┐    ┌────────────┐    ┌──────────────┐    ┌─────────┐
  │  Fill    │───▶│  Validate  │───▶│   Generate   │───▶│Download │
  │  Form    │    │  Inputs    │    │  Document    │    │  PDF    │
  └──────────┘    └────────────┘    └──────────────┘    └─────────┘
```

Each stage is described in detail in the sections below.

---

## 2. Complete Generate-Document Flow

This is the full end-to-end sequence from the user clicking **Create Document** to receiving the finished PDF.

```
User (Browser)          Blazor WASM             ASP.NET Core API        External Services
─────────────           ─────────────           ─────────────────       ─────────────────
Fill form fields
Click "Create Document"
                        AddTicket() flush
                        StartValidation()
                           │
                           ├──POST /api/jira/validate-tickets──▶ JiraController
                           │                                        │
                           │                                        ├──GetTicketAsync()──▶ Jira Cloud
                           │                                        │◀────ticket data──────
                           │                                        │  (or KeyNotFoundException)
                           │◀──── List<TicketValidationResult> ─────
                           │
                           ├──GET /api/git/validate?url=...──▶ GitController
                           │                                       │
                           │                                       ├──LibGit2Sharp.Clone()
                           │◀──── { valid, message } ─────────────
                           │
                           [All rows pass?]
                           │   YES
                           ▼
                        Generate()
                           │
                           ├──POST /api/documentation/generate──▶ DocumentationController
                           │      { sourceType, ticketIds,           │
                           │        repoUrl, branch,                 ├── GetTicketsAsync()──▶ Jira Cloud
                           │        outputFileName, docType }        ├── AnalyzeRepositoryAsync()──▶ Git repo
                           │                                         ├── GenerateDocumentAsync()
                           │                                         │      └──CallOpenAiAsync()──▶ Groq API
                           │                                         ├── GeneratePdfAsync()──▶ QuestPDF
                           │                                         │      └── SavePdf(outputPath)
                           │◀─── { success, fileName } ─────────────
                           │
                        UiState = Success
                        Add file to _generatedFiles list
                           │
User sees modal "✅ Done"
Click "Download"
                        DownloadPdf(fileName)
                           │
                           ├──GET /api/documentation/download/{fileName}──▶ DocumentationController
                           │◀─── FileContentResult (application/pdf) ────────
                           │
                        JS interop: anchor click → browser saves file
```

---

## 3. Validation Flow

Validation runs **before** document generation. It checks all inputs and short-circuits if any critical check fails.

### 3.1 Client-side pre-checks (ValidationService.cs)

These run entirely in the browser — no network calls.

| Check | Condition | Outcome |
|-------|-----------|---------|
| Source type selected | `_sourceType` is not empty | ✅ Pass / ❌ Fail |
| Output file name | Not empty, no path separators | ✅ Pass / ❌ Fail |
| Document type selected | `_docType` is not empty | ✅ Pass / ❌ Fail |
| JIRA ticket IDs present | At least one ID in `_ticketIds` | ✅ Pass / ❌ Fail |
| Git repo URL present | Not blank when Git source selected | ✅ Pass / ❌ Fail |

### 3.2 JIRA ticket server checks (per ticket)

Called via `POST /api/jira/validate-tickets`:

```
For each ticketId (distinct, uppercased):
  ┌──────────────────────────────────────────────────────────────┐
  │ Call JiraService.GetTicketAsync(ticketId)                    │
  │                                                              │
  │  ┌── Success ─────────────────────────────────────────────┐  │
  │  │ ticket.Exists = true                                   │  │
  │  │ message = "{Summary}  [{Status}]"                      │  │
  │  │                                                        │  │
  │  │ If docType == UserGuide:                               │  │
  │  │   status in {"done","complete","completed"}?           │  │
  │  │     YES → ValidationStatus.Pass                       │  │
  │  │     NO  → ValidationStatus.Fail                       │  │
  │  │           "Ticket has status '...' but User Guide      │  │
  │  │            can only be generated for Done/Complete"    │  │
  │  └────────────────────────────────────────────────────────┘  │
  │                                                              │
  │  ┌── KeyNotFoundException ────────────────────────────────┐  │
  │  │ ticket.Exists = false                                  │  │
  │  │ message = clean English from ExtractJiraApiError()     │  │
  │  │ e.g. "JIRA Ticket does not exist or you do not have   │  │
  │  │       permission to see it."                           │  │
  │  └────────────────────────────────────────────────────────┘  │
  │                                                              │
  │  ┌── Other Exception ─────────────────────────────────────┐  │
  │  │ ticket.Exists = false                                  │  │
  │  │ JiraControllerHelpers.ExtractReadableMessage()         │  │
  │  │   or fallback: "Could not check ticket '...'"          │  │
  │  └────────────────────────────────────────────────────────┘  │
  └──────────────────────────────────────────────────────────────┘
  
  onRowUpdated(rows) called → Blazor re-renders live status
```

### 3.3 Git repository server check

Called via `GET /api/git/validate?url=...&branch=...`:

```
GitController.ValidateRepository()
  │
  ├── string.IsNullOrWhiteSpace(url)?
  │     YES → { valid: false, message: "Repository URL is required" }
  │
  └── GitService.ValidateRepositoryAsync(url, branch)
        │
        ├── LibGit2Sharp: clone/fetch with timeout
        │
        ├── Success → { valid: true,  message: "Repository accessible" }
        └── Failure → { valid: false, message: ex.Message }
```

### 3.4 Validation decision gate

```
All validation rows checked.
  ├── Any row with Status == Fail?
  │     YES → canProceed = false
  │            Modal stays open showing failure rows
  │            "← Back to form" button appears
  │
  └── All Pass (or Warn)?
        canProceed = true → Generate() called automatically
```

---

## 4. Document Generation Flow

### 4.1 API entry point

`POST /api/documentation/generate`  
Body: `GenerateDocumentRequest { SourceType, TicketIds, RepoUrl, Branch, OutputFileName, DocumentationType }`

```
DocumentationController.Generate()
  │
  ├── [User Guide check]
  │     docType == UserGuide && tickets fetched?
  │       Any ticket NOT in {"done","complete","completed"}?
  │         YES → 400 BadRequest { message: "User Guide can only be generated for completed tickets..." }
  │
  ├── sourceType includes JIRA?
  │     JiraService.GetTicketsAsync(ticketIds)
  │       → List<JiraTicket> (throws if ALL tickets fail)
  │
  ├── sourceType includes Git?
  │     GitService.AnalyzeRepositoryAsync(repoUrl, branch)
  │       → GitRepositoryInfo
  │
  ├── GroqService.GenerateDocumentAsync(tickets, repoInfo, docType)
  │     → DocumentContent (structured JSON parsed from LLM)
  │
  ├── PdfService.GeneratePdfAsync(content, outputFileName, docType)
  │     → saved to wwwroot/generated/{outputFileName}.pdf
  │
  └── 200 OK { success: true, fileName: "...", downloadUrl: "..." }
      or
      500 / 400 { message: "..." }
```

### 4.2 JIRA data fetch detail

```
JiraService.GetTicketsAsync(ids)
  │
  For each id:
    ├── GetTicketAsync(id)
    │     ├── _jiraClient.Issues.GetIssueAsync(id)   [Atlassian SDK]
    │     ├── issue.GetCommentsAsync()
    │     ├── FetchSubTasksAsync(id)                 [REST API v3 directly]
    │     │     GET /rest/api/3/search/jql?jql=parent={id}
    │     └── ExtractAcceptanceCriteria(description)
    │
    ├── Success → add to tickets list
    └── Failure → add to failedIds list
  │
  ├── Some succeeded, some failed → log warning, return partial list
  └── All failed → throw InvalidOperationException
```

### 4.3 Git analysis detail

```
GitService.AnalyzeRepositoryAsync(repoUrl, branch)
  │
  ├── Clone repository to temp dir (LibGit2Sharp)
  ├── Read recent commits (last 50)
  ├── Enumerate changed files
  ├── Detect primary language (by file extension frequency)
  ├── Count contributors
  └── Return GitRepositoryInfo
        { RepoUrl, Branch, RecentCommits[], ChangedFiles[],
          PrimaryLanguage, ContributorCount, LastCommitDate }
```

---

## 5. AI Prompt & JSON Repair Flow

### 5.1 Prompt construction

```
GroqService.GenerateDocumentAsync()
  │
  ├── Build system prompt:
  │     "You are a technical documentation expert. Generate a {docType}
  │      document as a single valid JSON object with this exact schema: ..."
  │
  ├── Build user prompt:
  │     ┌── JIRA data present?
  │     │     Serialize tickets: Key, Summary, Description, Status,
  │     │                        AcceptanceCriteria, Comments, SubTasks
  │     │     Trim to ~500 tokens to stay within free-tier budget
  │     │
  │     └── Git data present?
  │           Serialize: RecentCommits, ChangedFiles, PrimaryLanguage
  │
  └── Call CallOpenAiAsync(systemPrompt, userPrompt)
```

### 5.2 Groq API call with retry & rate-limit handling

```
CallOpenAiAsync(system, user)
  │
  attempt = 1 to MaxRetries (3):
    │
    ├── _openAiClient.CompleteChatStreamingAsync(messages, options)
    │     Stream tokens → accumulate full response text
    │
    ├── HTTP 200 → break, return responseText
    │
    ├── HTTP 429 (rate limit):
    │     Parse "try again in Xs" or "try again in XmY.Ys"
    │     from Groq error message body
    │     │
    │     waitSeconds ≤ 65s && first 429?
    │       YES → await Task.Delay(waitMs + 1500ms)
    │              attempt--   ← don't consume a retry slot
    │              continue    ← retry same attempt number
    │       NO  → throw InvalidOperationException
    │              "Groq free-tier rate limit exceeded.
    │               llama-3.1-8b-instant allows 6,000 TPM.
    │               Please wait ~{N}s and try again."
    │
    └── HTTP 5xx (transient):
          Log warning, await exponential back-off, continue
          After MaxRetries → throw
```

### 5.3 JSON extraction & repair pipeline

The LLM response is plain text that (usually) contains JSON. Eight strategies are tried in order; the first successfully parsed, non-trivial result wins.

```
ExtractAndParseJson(responseText)
  │
  Candidates tried in order:
  │
  1. Raw response text as-is
  2. FixLiteralNewlinesInStrings(rawText)          ← repairs bare \n inside strings
  3. Text between first ``` and last ```            ← fenced code block
  4. Repaired version of #3
  5. Text between first ``` ``` and last ``` ```    ← generic fenced block
  6. Repaired version of #5
  7. Text from first '{' to last '}'               ← brace extraction
  8. Repaired version of #7
  │
  For each candidate:
    ├── JsonDocument.Parse(candidate)
    │     Fail → next candidate
    │
    └── TryDeserializeDocumentContent(candidate)
          Check: at least one non-trivial field populated?
            YES → return DocumentContent ✅
            NO  → next candidate
  │
  All candidates exhausted → throw JsonException
  "Could not extract valid JSON from LLM response"
```

**`FixLiteralNewlinesInStrings` logic:**

```
Walk each character, tracking inString / escaped state:
  - If inside string and ch == '\n' → emit "\\n"
  - If inside string and ch == '\r' → emit "\\r"
  - If inside string and ch == '\t' → emit "\\t"
  - Otherwise → emit ch as-is
```

---

## 6. Job Status Polling Flow

Long-running generation is tracked server-side so the Blazor client can poll for status updates.

```
POST /api/documentation/generate
  │
  ├── JobStatusService.CreateJob(jobId)
  │     Store: { JobId, Status=Running, CreatedAt, Message="" }
  │     (ConcurrentDictionary<string, JobEntry>)
  │
  ├── [async generation work]
  │     JobStatusService.UpdateJob(jobId, status, message)
  │
  └── JobStatusService.CompleteJob(jobId, fileName)
        Status = Completed | Failed

Blazor client polling:
  │
  Every 2 seconds while UiState == Generating:
    │
    GET /api/documentation/status/{jobId}
      │
      ├── 200 { status: "Running",   message: "Fetching JIRA tickets..." }
      ├── 200 { status: "Completed", fileName: "output.pdf" }
      └── 200 { status: "Failed",    message: "Rate limit exceeded..." }

Stale job cleanup:
  Background service (IHostedService) runs every 5 minutes.
  Removes entries older than 30 minutes from the dictionary.
```

---

## 7. PDF Generation Flow

```
PdfService.GeneratePdfAsync(DocumentContent content, string fileName, DocumentationType docType)
  │
  ├── Build QuestPDF Document:
  │     Page settings: A4, 2.5cm margins, Calibri/Arial font
  │     │
  │     ├── Header section
  │     │     Title (H1), subtitle, generation date
  │     │
  │     ├── Executive Summary section
  │     │     Paragraph text from content.ExecutiveSummary
  │     │
  │     ├── Sections[] loop
  │     │     For each DocumentSection:
  │     │       ├── Section heading (H2)
  │     │       ├── Section body text
  │     │       └── SubSections[] (H3 + body)
  │     │
  │     ├── Tables[] loop (if present)
  │     │     Column headers (bold, shaded)
  │     │     Data rows (alternating row colour)
  │     │
  │     └── Generated Files list (footer area)
  │           One row per file: name · date · type
  │
  └── Document.GeneratePdfAndSave(outputPath)
        Saved to: {wwwroot}/generated/{fileName}.pdf
```

---

## 8. File Download Flow

```
User clicks "⬇ Download" in the Blazor UI
  │
  ├── Blazor calls DownloadPdf(fileName)
  │
  ├── DocumentationApiService.GetDownloadUrlAsync(fileName)
  │     Returns: /api/documentation/download/{fileName}
  │
  ├── JS interop: IJSRuntime.InvokeVoidAsync("downloadFile", url, fileName)
  │     JavaScript:
  │       const a = document.createElement('a');
  │       a.href = url; a.download = fileName;
  │       document.body.appendChild(a); a.click();
  │       document.body.removeChild(a);
  │
  └── GET /api/documentation/download/{fileName}
        │
        ├── Build path: wwwroot/generated/{fileName}
        ├── File.Exists(path)?
        │     NO → 404 Not Found
        │
        └── PhysicalFile(path, "application/pdf", fileName)
              Browser receives file → Save dialog appears
```

---

## 9. File History Persistence Flow

```
Browser localStorage key: "docuGenius_generatedFiles"
Value: JSON array of GeneratedFileInfo objects

On page load (OnInitializedAsync):
  │
  └── JS interop: localStorage.getItem("docuGenius_generatedFiles")
        │
        ├── null / empty → _generatedFiles = []
        └── JSON string  → JsonSerializer.Deserialize<List<GeneratedFileInfo>>()

On successful generation:
  │
  ├── _generatedFiles.Insert(0, new GeneratedFileInfo
  │       { FileName, DocumentType, CreatedAt = DateTime.Now })
  │
  └── JS interop: localStorage.setItem("docuGenius_generatedFiles", serialized)

On "Clear History" click:
  │
  ├── _generatedFiles.Clear()
  └── JS interop: localStorage.removeItem("docuGenius_generatedFiles")

Structure of GeneratedFileInfo:
  {
    "fileName":     "MyApp_TechSpec_20260424.pdf",
    "documentType": "Technical Specification",
    "createdAt":    "2026-04-24T10:35:00"
  }
```

---

## 10. Error Handling Flows

### 10.1 JIRA ticket not found

```
User enters non-existent ticket ID (e.g. "ABC-999")
  │
  Validation phase:
    JiraService.GetTicketAsync("ABC-999")
      │
      Atlassian SDK throws generic Exception:
        Message contains: "Response Content: {"errorMessages":
          ["Issue does not exist or you do not have permission to see it."],
          "errors":{}}"
      │
      ExtractJiraApiError(ex.Message):
        ├── Find first '{' in message
        ├── JsonDocument.Parse(json fragment)
        ├── Extract errorMessages[0]
        ├── Replace "Issue " → "JIRA Ticket "
        └── Return "JIRA Ticket does not exist or you do not have permission to see it."
      │
      Throw KeyNotFoundException(friendlyMessage)
      │
    JiraController catches KeyNotFoundException:
      item.Exists  = false
      item.Message = "JIRA Ticket does not exist or you do not have permission to see it."
      │
    ValidationService row: Status = Fail, Message = above text
    canProceed = false
    Modal shows "⚠️ Some checks failed" with ❌ row
    "← Back to form" button appears
```

### 10.2 Groq rate limit (429)

```
GroqService.CallOpenAiAsync()
  │
  ClientResultException (HTTP 429):
    Message: "... try again in 45.2s ..."
    │
    ParseRetryAfterSeconds("... try again in 45.2s ...")
      → 45.2 seconds
    │
    First 429 && waitSeconds ≤ 65?
      YES:
        Log "Groq 429 — free-tier token bucket full. Auto-waiting 45.2s..."
        await Task.Delay(46700ms)   ← 45.2s + 1.5s buffer
        attempt--                   ← retry same slot
        continue
      │
      Retry succeeds → normal flow resumes
      │
      Retry also 429 (or wait > 65s):
        throw InvalidOperationException:
          "Groq free-tier rate limit exceeded. The llama-3.1-8b-instant model
           allows 6,000 tokens per minute on the free plan, and this request
           used approximately 4,500 tokens. Please wait about 46 seconds and
           try again."
  │
  DocumentationController catches → 500 { message: "Groq free-tier rate limit..." }
  │
  DocumentationApiService:
    TryExtractMessage(errorBody) → extracts "message" field
    Returns GenerateResult { Success=false, Error="Groq free-tier rate limit..." }
  │
  Blazor UI: UiState = Error, _showDialog = true
  Modal shows: "❌ Error" + friendly message (no raw JSON)
```

### 10.3 Generation failure (general)

```
Any unhandled exception in DocumentationController.Generate()
  │
  500 InternalServerError { message: ex.Message }
  │
  DocumentationApiService.GenerateDocumentAsync():
    response.IsSuccessStatusCode == false
    errorBody = await response.Content.ReadAsStringAsync()
    friendly = TryExtractMessage(errorBody)   ← extracts "message" field
    return GenerateResult { Success=false,
                            Error = friendly ?? "Something went wrong (HTTP 500). Please try again." }
  │
  Home.razor Generate():
    _state = UiState.Error
    _errorMessage = result.Error
    _showDialog = true   ← re-show dialog even if user hid it
```

### 10.4 JSON parse failure (LLM response)

```
Groq returns malformed response (all 8 repair strategies fail):
  │
  GroqService throws JsonException
    "Could not extract valid JSON from LLM response after all repair attempts."
  │
  → bubbles up through GenerateDocumentAsync → DocumentationController → 500
  → handled by flow 10.3 above
```

### 10.5 All JIRA tickets fail to fetch (generation phase)

```
JiraService.GetTicketsAsync():
  Every ticket threw an exception
  tickets.Count == 0 && failedIds.Count > 0
  │
  throw InvalidOperationException:
    "Could not fetch any of the requested JIRA ticket(s): ABC-1, ABC-2.
     Please verify the ticket IDs exist and that your JIRA credentials
     have access to them."
  │
  → 500 from DocumentationController
  → handled by flow 10.3 above
```

---

## 11. UI State Machine

The Blazor `Home.razor` component manages a strict linear state machine. State transitions are triggered by user actions and async callbacks.

```
                     ┌────────────────────────────────────────────────┐
                     │                  UiState                       │
                     └────────────────────────────────────────────────┘

        ┌──────────────────────────────────────────────────────────────┐
        │  FORM                                                        │
        │  ─────────────────────────────────────────────────────────  │
        │  User fills: source type, ticket IDs, repo URL,             │
        │              output file name, document type                │
        │                                                              │
        │  CanSubmit = sourceType set                                  │
        │            && (not JIRA || ticketIds.Count>0 || ticketInput) │
        │            && (not Git  || repoUrl not blank)               │
        │                                                              │
        │  "Create Document" button enabled when CanSubmit == true    │
        └──────────────────────┬───────────────────────────────────────┘
                               │ Click "Create Document"
                               ▼
        ┌──────────────────────────────────────────────────────────────┐
        │  VALIDATING                                                  │
        │  ─────────────────────────────────────────────────────────  │
        │  Modal open: "🔍 Checking inputs…"                          │
        │  Live rows update as each check completes                   │
        │  _validationRunning = true                                   │
        └──────────┬───────────────────────────────────┬──────────────┘
                   │ All Pass                           │ Any Fail
                   ▼                                   ▼
        ┌──────────────────────┐           ┌──────────────────────────┐
        │  GENERATING          │           │  VALIDATING (failed)     │
        │  ──────────────────  │           │  ──────────────────────  │
        │  Modal: spinner +    │           │  Modal: "⚠️ Some checks  │
        │  status message      │           │   failed"                │
        │  "Hide" button       │           │  Failure rows shown      │
        │  (closes modal,      │           │  "← Back to form" button │
        │   keeps generating)  │           └───────────┬──────────────┘
        └──────────┬───────────┘                       │ Click "← Back to form"
                   │                                   ▼
                   │                        ┌──────────────────────┐
                   │                        │  FORM (all cleared)  │
                   │                        └──────────────────────┘
                   │ Success
                   ▼
        ┌──────────────────────────────────────────────────────────────┐
        │  SUCCESS                                                     │
        │  ─────────────────────────────────────────────────────────  │
        │  Modal: "✅ Document created!"                               │
        │  Download button active                                     │
        │  "Close" button → clears form, closes modal                 │
        └──────────────────────────────────────────────────────────────┘
                   │ Failure
                   ▼
        ┌──────────────────────────────────────────────────────────────┐
        │  ERROR                                                       │
        │  ─────────────────────────────────────────────────────────  │
        │  Modal: "❌ Error" + friendly message                        │
        │  "Close" button → returns to FORM                           │
        └──────────────────────────────────────────────────────────────┘
```

### State transition table

| Current State | Trigger | Next State | Side Effect |
|---------------|---------|------------|-------------|
| Form | Click "Create Document" | Validating | Open modal, start validation |
| Validating | All checks pass | Generating | Start async generation |
| Validating | Any check fails | Validating (failed) | Show failure rows |
| Validating (failed) | Click "← Back to form" | Form | Clear all form fields, close modal |
| Generating | Generation succeeds | Success | Add file to history |
| Generating | Click "Hide" | Generating | Close modal, generation continues |
| Generating (hidden) | Generation succeeds | Form | Silently add file to history |
| Generating (hidden) | Generation fails | Error | Re-open modal with error |
| Success | Click "Close" | Form | Clear all form fields |
| Success | Click "Download" | Success | Trigger browser file download |
| Error | Click "Close" | Form | Clear error message |

---

*Document generated as part of the DocuGenious project documentation suite.*
*See also: [Architecture.md](Architecture.md)*
