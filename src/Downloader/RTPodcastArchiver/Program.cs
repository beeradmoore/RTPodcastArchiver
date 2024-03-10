
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Xml;
using RTPodcastArchiver;

// Location you want to download podcasts to.
var basePath = "/Volumes/Storage/RT Podcast Archive";
if (Directory.Exists(basePath) == false)
{
	Directory.CreateDirectory(basePath);
}

// Load the podcasts json which contians your user specific URLs
var podcasts = new List<Podcast>();

if (File.Exists("podcasts.json"))
{
	var tempPodcasts = JsonSerializer.Deserialize<List<Podcast>>(File.ReadAllText("podcasts.json"));
	if (tempPodcasts != null)
	{
		podcasts.AddRange(podcasts);
	}
}
else
{
	// If you don't have any podcast URLs you need to get the RSS links from your account on
	// https://roosterteeth.supportingcast.fm/subscription/type/podcast
	// and then manually add the data here. This will generate a podcasts.json file in
	// your execution folder (eg. bin/Debug/). This fill will then be used going forwards.
	// If you want to add a new podcast you need to edit your podcasts.json folder, or delete
	// it and re-generate it with your code below.
	/*
	podcasts.Add(new Podcast()
	{
		Name = "F**kface",
		Url = "https://roosterteeth.supportingcast.fm/content/eyABC.....123.rss",
	});
	podcasts.Add(new Podcast()
	{
		Name = "Black Box Down",
		Url = "https://roosterteeth.supportingcast.fm/content/eyXYZ.....789.rss",
	});
	*/
}

var httpClient = new HttpClient();

string MakeSafeFilename(string filename, char replaceChar)
{
	foreach (char c in Path.GetInvalidFileNameChars())
	{
		if (filename.Contains(c))
		{
			filename = filename.Replace(c, replaceChar);
		}
	}
	return filename;
}

// Go through each podcast.
foreach (var podcast in podcasts)
{
	Console.WriteLine($"Loading {podcast.Name}");
	
	// Create the storage path if it does not already exist
	var podcastPath = Path.Combine(basePath, podcast.Name);
	if (Directory.Exists(podcastPath) == false)
	{
		Directory.CreateDirectory(podcastPath);
	}

	// Download podcast manifest.
	var podcastResponse = await httpClient.GetAsync(podcast.Url);
	if (podcastResponse.StatusCode != HttpStatusCode.OK)
	{
		Console.WriteLine("Error: Unable to download podcast RSS feed.");
		continue;
	}
	
	// Save this data to podcast.xml
	// TODO: backup old manifest.
	var podcastXmlPath = Path.Combine(podcastPath, "podcast.xml");
	var podcastData = await podcastResponse.Content.ReadAsStringAsync();
	File.WriteAllText(podcastXmlPath, podcastData);
	
	// Read this RSS file to find out where we need to download data from.
	var xmlDoc = new XmlDocument();
	xmlDoc.Load(podcastXmlPath);

	// This is the cover of the podcast. Download it to cover.jpg (or whatever the extension is)
	var imageNode = xmlDoc.DocumentElement?.SelectSingleNode("/rss/channel/image/url");
	if (imageNode != null)
	{
		var coverUri = new Uri(imageNode.InnerText);
		var coverExtension = Path.GetExtension(coverUri.AbsolutePath);
		var coverPath = Path.Combine(podcastPath, $"cover{coverExtension}");
		if (File.Exists(coverPath) == false)
		{
			Console.WriteLine("Downloading cover");
			using (var stream = await httpClient.GetStreamAsync(coverUri.AbsoluteUri))
			{
				using (var fileStream = File.Create(coverPath))
				{
					await stream.CopyToAsync(fileStream);
				}
			}
		}
		else
		{
			//Console.WriteLine("Cover already exists, skipping");
		}

		// The cover URL has resizing attributes on it as well, so lets get the original url without attributes and save that.
		if (coverUri.AbsoluteUri.Contains('?') == true)
		{
			var coverOriginalPath = Path.Combine(podcastPath, $"cover_original{coverExtension}");
			if (File.Exists(coverOriginalPath) == false)
			{
				Console.WriteLine("Downloading original cover");
				using (var stream = await httpClient.GetStreamAsync($"https://{coverUri.Host}{coverUri.AbsolutePath}"))
				{
					using (var fileStream = File.Create(coverOriginalPath))
					{
						await stream.CopyToAsync(fileStream);
					}
				}
			}
			else
			{
				//Console.WriteLine("Original cover already exists, skipping");
			}
		}
		else
		{
			//Console.WriteLine("Cover is not modified, so original cover is not needed.");
		}
	}
	else
	{
		Console.WriteLine("Error: Could not get podcast cover images.");
	}
	
	// Now to get each episode and download those.
	var itemNodes = xmlDoc.DocumentElement?.SelectNodes("/rss/channel/item") as XmlNodeList;
	if (itemNodes == null)
	{
		Console.WriteLine("Error: No podcast episodes found.");
		continue;
	}
	
	// I forget why, but I moved items from the itemsNode into a list in reverse order.
	var items = new List<XmlNode>();
	for (var i = itemNodes.Count - 1; i >= 0; --i)
	{
		var item = itemNodes[i];
		if (item != null)
		{
			items.Add(item);
		}
	}

	// This fileSummaryList will contain lookup data for the other tool used to upload the podcasts to Internet Archive.
	var fileSummaryList = new List<FileSummary>();
	foreach (var item in items)
	{
		var title = item["title"]?.InnerText;
		if (String.IsNullOrEmpty(title) == true)
		{
			Console.WriteLine("Error: Title is empty.");
			return;
		}
		
		var guid = item["guid"]?.InnerText;
		if (String.IsNullOrEmpty(guid) == true)
		{
			Console.WriteLine("Error: Guid is empty.");
			return;
		}
		
		// Not all episodes have an episode number or a season number, so we do our best to get them if they exist.
		var episodeInt = -1;
		var seasonInt = -1;
		try
		{
			var episodeText = item["itunes:episode"]?.InnerText;
			if (String.IsNullOrEmpty(episodeText) == false)
			{
				episodeInt = Int32.Parse(episodeText);
			}
		}
		catch (Exception)
		{
			// NOOP
		}

		try
		{
			var seasonText = item["itunes:season"]?.InnerText;
			if (String.IsNullOrEmpty(seasonText) == false)
			{
				seasonInt = Int32.Parse(seasonText);
			}
		}
		catch (Exception)
		{
			// NOOP
		}

		// Prefix is empty to start of with.
		var episodeSeasonPrefix = String.Empty;
		
		// If there is a season number we use it.
		if (seasonInt >= 0)
		{
			// If there is an episode number we use it.
			if (episodeInt >= 0)
			{
				episodeSeasonPrefix = $"S{seasonInt:00} E{episodeInt} ";
			}
			else
			{
				episodeSeasonPrefix = $"S{seasonInt:00} ";
			}
		}
		else
		{
			// If there is no season number we just use the episode number if it exists.
			if (episodeInt >= 0)
			{
				episodeSeasonPrefix = $"E{episodeInt} ";
			}
		}

		// We also want to get the publish date as this is what we prefix episode files with.
		var pubDateString = item.SelectSingleNode("pubDate")?.InnerText;
		if (String.IsNullOrEmpty(pubDateString) == true)
		{
			Console.WriteLine("Error: pubDate not found.");
			return;
		}
		var pubDate = DateTime.Parse(pubDateString).ToUniversalTime();
		var enclosure = item.SelectSingleNode("enclosure");
		if (enclosure == null)
		{
			Console.WriteLine("Error: enclosure not found.");
			return;
		}

		// Enclosure tag is what contains the actual url.
		var enclosureUriString = enclosure.Attributes?.GetNamedItem("url")?.Value;
		if (String.IsNullOrEmpty(enclosureUriString) == true)
		{
			Console.WriteLine("Error: enclosure url not found.");
			return;
		}
		
		var enclosureUri = new Uri(enclosureUriString);
		
		// Now we create thee episode filename, this could be something like,
		// 2021-03-27 - S4 E28 Ha, that good one.mp3
		// 2021-03-28 - E271 Yep that was good to.mp3
		// 2021-03-29 - That one where we all cried.mp3
		var enclosureExtension = Path.GetExtension(enclosureUri.AbsolutePath);
		var episodeFilename = MakeSafeFilename($"{pubDate:yyyy-MM-dd} - {episodeSeasonPrefix}{title}{enclosureExtension}", '-');
		
		// This is the final path of where we save it.
		var episodePath = Path.Combine(podcastPath, episodeFilename);

		// We also want to get the file length metadata. 
		// Fun fact, this is sometimes wrong.
		var enclosureLength = -1L;
		var enclosureLengthString = enclosure.Attributes?.GetNamedItem("length")?.Value;
		if (String.IsNullOrEmpty(enclosureLengthString) == false)
		{
			enclosureLength = long.Parse(enclosureLengthString);
		}
		
		// We build the file summary item and add it to our list to be saved later.
		// We separate ReportedLength and ActualLength as we update the manifest in the
		// other application to be correct.
		var fileSummary = new FileSummary()
		{
			Guid = guid,
			LocalFilename = episodePath,
			ReportedLength = enclosureLength,
		};
		fileSummaryList.Add(fileSummary);

		// Now to see if we should download the episode or if we already have it.
		var shouldDownload = true;

		// First, does the file exist?
		if (File.Exists(episodePath) == true)
		{
			// How big is the file (in bytes)
			var fileInfo = new FileInfo(episodePath);
			// Save this for later.
			fileSummary.ActualLength = fileInfo.Length;
			
			// If the length reported from the manifest and the local length is the same, we likely
			// have the same file.
			// This isn't always the case and we should check ETag headers if they exist. 
			// But 60 days is 60 days, so what you going to do.
			if (fileInfo.Length == enclosureLength)
			{
				shouldDownload = false;
			}
			else
			{
				// For some reason Black Box Down file size does not match actual file size.
				// So instead we just have to assume we did get all files downloaded correctly.
				if (podcast.Name == "Black Box Down (FIRST Member Ad-Free)")
				{
					shouldDownload = false;
				}
			}
		}

		// Now if we should download it we add the RemoteUrl. This is the location
		// of the mp3 on the internet. If this value is not set we know we should
		// not be downloading.
		if (shouldDownload)
		{
			fileSummary.RemoteUrl = enclosureUri.AbsoluteUri;
		}
		else
		{
			//Console.WriteLine($"File already exists, skipping, {episodeFilename}");
		}
	}
	
	// We have now looked at every episode for this podcast. Now to download them all.
	// We use a Parallel.ForEachAsync so we can download them in parallel to speed things up.
	var parallelOptions = new ParallelOptions()
	{
		MaxDegreeOfParallelism = 8,
	};
	await Parallel.ForEachAsync(fileSummaryList, parallelOptions, async (fileSummary, token) =>
	{
		// Only download if there is a remote URL.
		if (String.IsNullOrEmpty(fileSummary.RemoteUrl) == true)
		{
			return;
		}
		
		var episodeFilename = Path.GetFileName(fileSummary.LocalFilename);
		Console.WriteLine($"Downloading {episodeFilename}");

		try
		{
			// Use this stream to download the data.
			using (var stream = await httpClient.GetStreamAsync(fileSummary.RemoteUrl))
			{
				using (var fileStream = File.Create(fileSummary.LocalFilename))
				{
					await stream.CopyToAsync(fileStream);

					fileSummary.ActualLength = stream.Length;
				}
			}
		}
		catch (Exception err)
		{
			Console.WriteLine($"Error: {err.Message}");
			Debugger.Break();
		}
	});

	// Now that we have downloaded (or at least attempted to download) each of the episodes we
	// want to save a summary.json in the folder so we have a reference of what exists.
	var summaryJson = Path.Combine(podcastPath, "summary.json");
	using (var fileStream = File.Create(summaryJson))
	{
		JsonSerializer.Serialize(fileStream, fileSummaryList, new JsonSerializerOptions() { WriteIndented = true });
	}
}

// Done. Enjoy your local podcasts. Run this again if you want to check for any updates.
Console.WriteLine("Done.");