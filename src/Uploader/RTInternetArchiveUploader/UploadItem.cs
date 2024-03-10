namespace RTInternetArchiveUploader;

public class UploadItem
{
	public string LocalPath { get; init; }
	public string RemotePath { get; init; }

	public UploadItem(string localPath, string remotePath)
	{
		LocalPath = localPath;
		RemotePath = remotePath;
	}
}