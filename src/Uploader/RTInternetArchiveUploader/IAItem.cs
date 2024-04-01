using SQLite;

namespace RTInternetArchiveUploader;

public class IAItem
{
	[Indexed]
	public string Identifier { get; set; } = string.Empty;

	[Indexed]
	public string FileName { get; set; } = string.Empty;
}