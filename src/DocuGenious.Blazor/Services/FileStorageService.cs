using System.Text.Json;

public class FileStorageService
{
	private readonly string _filePath = Path.Combine("wwwroot", "data", "files.json");

	public async Task<List<FileRecord>> GetFilesAsync()
	{
		if (!File.Exists(_filePath))
			return new List<FileRecord>();

		var json = await File.ReadAllTextAsync(_filePath);
		return JsonSerializer.Deserialize<List<FileRecord>>(json) ?? new();
	}

	public async Task SaveFilesAsync(List<FileRecord> files)
	{
		var json = JsonSerializer.Serialize(files, new JsonSerializerOptions
		{
			WriteIndented = true
		});

		Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
		await File.WriteAllTextAsync(_filePath, json);
	}
	public class FileRecord
	{
		public string FileName { get; set; } = "";
		public DateTime CreatedOn { get; set; }
	}
}