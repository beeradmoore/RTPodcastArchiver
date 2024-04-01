using System.Text.Json.Serialization;

namespace RTPodcastArchiver;

public class Podcast
{
	[JsonPropertyName("name")]
	public string Name { get; set; } = String.Empty;

	[JsonPropertyName("url")]
	public string Url { get; set; } = String.Empty;

	[JsonPropertyName("ia_identifier")]
	public string IAIdentifier { get; set; } = String.Empty;

	[JsonPropertyName("enabled")]
	public bool IsEnabled { get; set; } = false;
	
	public Podcast()
	{
		
	}

	public Podcast(string name)
	{
		Name = name;
		IsEnabled = true;
	}
}