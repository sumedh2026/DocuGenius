using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocuGenious.Core.Interfaces;
using DocuGenious.Core.Models;
using OpenAI;
using OpenAI.Chat;

namespace DocuGenious.Integration.Groq;

public class GroqService : IGroqService
{
	private readonly ChatClient _chatClient;

	public GroqService(string apiKey, string baseUrl, string model)
	{
		var options = new OpenAIClientOptions
		{
			Endpoint = new Uri(baseUrl),
			NetworkTimeout = TimeSpan.FromMinutes(5)
		};

		var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
		_chatClient = client.GetChatClient(model);
	}

	public async Task<AnalysisResult> AnalyzeAsync(
		string jiraContext,
		string gitContext,
		DocumentationType docType,
		string? additionalContext = null)
	{
		if (string.IsNullOrWhiteSpace(jiraContext) &&
			string.IsNullOrWhiteSpace(gitContext))
			throw new ArgumentException("At least one source context must be provided.");

		// 1️⃣ Chunk
		var chunks = new List<TextChunk>();
		if (!string.IsNullOrWhiteSpace(jiraContext))
			chunks.AddRange(Chunk(jiraContext, "JIRA"));
		if (!string.IsNullOrWhiteSpace(gitContext))
			chunks.AddRange(Chunk(gitContext, "GIT"));

		// 2️⃣ Retrieve relevant chunks
		var retrieved = Retrieve(chunks, docType, topK: 8);

		// 3️⃣ Prompt
		var prompt = BuildPrompt(docType, retrieved, additionalContext);

		// 4️⃣ Groq call
		var messages = new List<ChatMessage>
		{
			new SystemChatMessage(GetSystemPrompt(docType)),
			new UserChatMessage(prompt)
		};

		var options = new ChatCompletionOptions
		{
			Temperature = 0.4f,
			MaxOutputTokenCount = 2500
		};

		var response = await _chatClient.CompleteChatAsync(
			messages, options, CancellationToken.None);

		var text = string.Concat(
			response.Value.Content
				.Where(p => p.Kind == ChatMessageContentPartKind.Text)
				.Select(p => p.Text));

		return new AnalysisResult
		{
			ExecutiveSummary = text,
			DocumentationType = docType,
			SourceInfo = "JIRA + GIT (chunked)",
			GeneratedAt = DateTime.UtcNow
		};
	}

	// ───────── Chunking ─────────

	private static IEnumerable<TextChunk> Chunk(string text, string source)
	{
		const int maxLen = 450;
		var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		var sb = new StringBuilder();
		int index = 0;

		foreach (var line in lines)
		{
			if (sb.Length + line.Length > maxLen)
			{
				yield return CreateChunk(sb.ToString(), source, index++);
				sb.Clear();
			}
			sb.AppendLine(line.Trim());
		}

		if (sb.Length > 0)
			yield return CreateChunk(sb.ToString(), source, index);
	}

	private static TextChunk CreateChunk(string content, string source, int index) =>
		new()
		{
			Id = $"{source}-{index}",
			Source = source,
			Content = content.Trim()
		};

	// ───────── Retrieval ─────────

	private static List<TextChunk> Retrieve(
		IEnumerable<TextChunk> chunks,
		DocumentationType docType,
		int topK)
	{
		var keywords = Keywords(docType);

		return chunks
			.Select(c => new
			{
				Chunk = c,
				Score = keywords.Count(k =>
					c.Content.Contains(k, StringComparison.OrdinalIgnoreCase))
			})
			.Where(x => x.Score > 0)
			.OrderByDescending(x => x.Score)
			.Take(topK)
			.Select(x => x.Chunk)
			.ToList();
	}

	private static List<string> Keywords(DocumentationType docType) =>
		docType switch
		{
			DocumentationType.TechnicalDocumentation =>
				new() { "architecture", "config", "dependency", "setup" },

			DocumentationType.UserGuide =>
				new() { "how", "step", "use", "workflow" },

			DocumentationType.ApiDocumentation =>
				new() { "endpoint", "request", "response", "auth" },

			DocumentationType.ArchitectureOverview =>
				new() { "component", "flow", "integration" },

			_ => new() { "overview", "feature" }
		};

	// ───────── Prompting ─────────

	private static string BuildPrompt(
		DocumentationType docType,
		IEnumerable<TextChunk> chunks,
		string? additionalContext)
	{
		var sb = new StringBuilder();

		if (!string.IsNullOrWhiteSpace(additionalContext))
		{
			sb.AppendLine($"Additional context: {additionalContext}");
			sb.AppendLine();
		}

		sb.AppendLine("SOURCE DATA:");
		foreach (var c in chunks)
		{
			sb.AppendLine($"--- [{c.Source}] ---");
			sb.AppendLine(c.Content);
		}

		sb.AppendLine();
		sb.AppendLine("Output JSON:");
		sb.AppendLine(GetJsonSchema(docType));

		return sb.ToString();
	}

	private static string GetSystemPrompt(DocumentationType docType)
	{
		var role = docType switch
		{
			DocumentationType.UserGuide => "technical writer for non-technical users",
			DocumentationType.TechnicalDocumentation => "senior software engineer",
			DocumentationType.ApiDocumentation => "API documentation specialist",
			DocumentationType.ArchitectureOverview => "principal software architect",
			_ => "technical writer"
		};

		return $"You are a {role}. Return ONLY valid JSON. No markdown. No assumptions.";
	}

	private static string GetJsonSchema(DocumentationType docType) =>
		"""{"executiveSummary":"","content":""}""";

	private sealed class TextChunk
	{
		public string Id { get; init; } = "";
		public string Source { get; init; } = "";
		public string Content { get; init; } = "";
	}
}