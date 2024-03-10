using System.Text.Json.Serialization;

namespace RTPodcastArchiver;

public class Podcast
{
	[JsonPropertyName("name")]
	public string Name { get; set; } = String.Empty;

	[JsonPropertyName("url")]
	public string Url { get; set; } = String.Empty;
}