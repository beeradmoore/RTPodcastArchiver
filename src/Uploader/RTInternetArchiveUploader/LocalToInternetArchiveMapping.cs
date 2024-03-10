using System.Text.Json.Serialization;

namespace RTInternetArchiveUploader;

public class LocalToInternetArchiveMapping
{
	[JsonPropertyName("local_name")]
	public string LocalName { get; set; } = String.Empty;
	
	[JsonPropertyName("local_folder")]
	public string LocalFolder { get; set; } = String.Empty;
	
	[JsonPropertyName("ia_identifier")]
	public string IAIdentifier { get; set; } = String.Empty;

	[JsonIgnore]
	public List<UploadItem> FilesToUpload { get; } = new List<UploadItem>();
}