using SQLite;

namespace RTPodcastArchiver;

public class PodcastEpisode
{
	[PrimaryKey]
	public string PodcastName_Guid { get; set; } = string.Empty;

	[Indexed]
	public string PodcastName { get; set; } = string.Empty;

	[Indexed]
	public string FileName { get; set; } = string.Empty;

	public string Guid { get; set; }
	
	public long Size { get; set; } = -1;

	public string MD5Hash { get; set; } = string.Empty;

	public DateTime DateAdded { get; set; } = DateTime.MinValue;
	
	public DateTime DateLastDownloaded { get; set; } = DateTime.MinValue;

	public PodcastEpisode()
	{
		
	}
	
	public PodcastEpisode(string podcastName, string guid)
	{
		PodcastName_Guid = $"{podcastName}_{guid}";
		PodcastName = podcastName;
		Guid = guid;
	}
}