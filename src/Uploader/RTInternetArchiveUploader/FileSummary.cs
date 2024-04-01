using System.Text.Json.Serialization;

namespace RTInternetArchiveUploader;

public class FileSummary
{
	[JsonPropertyName("guid")]
	public string Guid { get; set; } = String.Empty;
	
	[JsonPropertyName("local_filename")]
	public string LocalFilename { get; set; } = String.Empty;
	
	[JsonPropertyName("podcast_name")]
	public string PodcastName { get; set; } = String.Empty;

	[JsonPropertyName("reported_length")]
	public long ReportedLength { get; set; } = -1L;
	
	[JsonPropertyName("actual_length")]
	public long ActualLength { get; set; } = -1L;

	[JsonIgnore]
	public string RemoteUrl { get; set; } = String.Empty;
}