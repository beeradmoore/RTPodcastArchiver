using System;
using System.CommandLine;
using System.CommandLine.Invocation;

using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Serilog;
using Serilog.Core;

namespace RTPodcastArchiver;

class Program
{
	static HttpClient _httpClient = new HttpClient();
	
	static async Task<int> Main(string[] args)
	{
		var rootCommand = new RootCommand("Downloads the entire Rooster Teeth FIRST podcast archive.\nNOTE: Your own user-specific podcast urls are required for this tool to work.");
		var outputOption = new Option<string>(new string[] { "--output", "-o" }, () =>
		{
			var envRTPodcastArchiverPath = Environment.GetEnvironmentVariable("RT_PODCAST_ARCHIVER_PATH") ?? String.Empty;
			if (String.IsNullOrEmpty(envRTPodcastArchiverPath) == false)
			{
				return envRTPodcastArchiverPath;
			}
			return Path.Combine(Directory.GetCurrentDirectory(), "output");
		}, "The archive output folder path. You can also set the environment variable RT_PODCAST_ARCHIVER_PATH and omit output path to accomplish the same thing.");
		rootCommand.AddOption(outputOption);
		rootCommand.SetHandler(RunAsync, outputOption);
		return await rootCommand.InvokeAsync(args);
	}
	
	static string MakeSafeFilenameNew(string filename)
	{
		// Special cases for podcasts that "use a format // like this"
		filename = filename.Replace("//", "-");
		
		var cleanFileName = Regex.Replace(filename, "[^a-zA-Z0-9._\\-() ]+", String.Empty);
		Log.Information($"{filename} -> {cleanFileName}");
		return cleanFileName;
	}


	static string MakeSafeFilenameOld(string filename, char replaceChar)
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

	static async Task<int> RunAsync(string outputPath)
	{
		Log.Information($"Using output path: {outputPath}");

		string archivePath = String.Empty;
		string logPath = String.Empty;
		try
		{
			if (Directory.Exists(outputPath) == false)
			{
				Directory.CreateDirectory(outputPath);
			}


			archivePath = Path.Combine(outputPath, "archive");
			if (Directory.Exists(archivePath) == false)
			{
				Directory.CreateDirectory(archivePath);
			}


			logPath = Path.Combine(outputPath, "logs");
			if (Directory.Exists(logPath) == false)
			{
				Directory.CreateDirectory(logPath);
			}
		}
		catch (Exception err)
		{
			Log.Information($"ERROR: Could not create output paths. ({err.Message})");
			return 1;
		}

		// Setup logger
		Log.Logger = new LoggerConfiguration()
			.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss}] {Message}{NewLine}{Exception}")
			.WriteTo.File(Path.Combine(logPath, "rt_podcast_archiver_.log"))
			.CreateLogger();

		// Load the podcasts.json which contains your user specific URLs
		var podcasts = new List<Podcast>();
		var podcastsJson = Path.Combine(outputPath, "podcasts.json");
		if (File.Exists(podcastsJson) == false)
		{
			Log.Information("podcasts.json was not found so a template is being created for here:");
			Log.Information(podcastsJson);
			Log.Information($"Please edit your podcasts.json file in the following way.");
			Log.Information("");
			Log.Information("Name:");
			Log.Information("\"name\": \"Some Podcast\"");
			Log.Information("Rename the \"Some Podcast\" name to be what you want this podcast to be called.");
			Log.Information("If you use a podcast name like \"F**kface\" but then later rename it to \"fkface\"");
			Log.Information("this will result in all episodes being re-downloaded. If wish to rename a podcast");
			Log.Information("name then please also rename its respective output folder.");
			Log.Information("");
			Log.Information("Url:");
			Log.Information("\"url\": \"\"");
			Log.Information("Add the specific podcast RSS feed between the \" marks.");
			Log.Information("To get your user specific podcast urls please log into your Rooster Teeth podcasts account:");
			Log.Information("https://roosterteeth.supportingcast.fm/subscription/type/podcast");
			Log.Information("and copy/paste the RSS feed URL to the appropriate podcast.");
			Log.Information("Any \"url\" left blank will not be downloaded.");
			Log.Information("Example of what a single podcast entry should look like:");
			Log.Information("");
			Log.Information("...");
			Log.Information("{");
			Log.Information("    \"name\": \"Red Web\"");
			Log.Information("    \"url\": \"https://roosterteeth.supportingcast.fm/content/eyABC.....123.rss\"");
			Log.Information("}");
			Log.Information("...\n");
			Log.Information("");
			
			podcasts.Add(new Podcast("30 Morbid Minutes"));
			podcasts.Add(new Podcast("A Simple Talk"));
			podcasts.Add(new Podcast("ANMA"));
			podcasts.Add(new Podcast("Always Open"));
			podcasts.Add(new Podcast("Annual Pass"));
			podcasts.Add(new Podcast("Beneath"));
			podcasts.Add(new Podcast("Black Box Down"));
			podcasts.Add(new Podcast("D&D, but..."));
			podcasts.Add(new Podcast("DEATH BATTLE Cast"));
			podcasts.Add(new Podcast("F**kface"));
			podcasts.Add(new Podcast("Face Jam"));
			podcasts.Add(new Podcast("Funhaus Podcast"));
			podcasts.Add(new Podcast("Good Morning From Hell"));
			podcasts.Add(new Podcast("Hypothetical Nonsense"));
			podcasts.Add(new Podcast("Must Be Dice"));
			podcasts.Add(new Podcast("OT3 Podcast"));
			podcasts.Add(new Podcast("Off Topic"));
			podcasts.Add(new Podcast("Red Web"));
			podcasts.Add(new Podcast("Rooster Teeth Podcast"));
			podcasts.Add(new Podcast("Ship Hits The Fan"));
			podcasts.Add(new Podcast("So... Alright"));
			podcasts.Add(new Podcast("Tales from the Stinky Dragon"));
			podcasts.Add(new Podcast("The Dogbark Podcast"));
			podcasts.Add(new Podcast("The Most"));
			podcasts.Add(new Podcast("Trash for Trash"));
			
			try
			{
				using (var fileStream = File.Create(podcastsJson))
				{
					JsonSerializer.Serialize(fileStream, podcasts, new JsonSerializerOptions() { WriteIndented = true });
				}
			}
			catch (Exception err)
			{
				Log.Error(err, "Could not create podcast.json");
				return 1;
			}
			return 0;
		}
		
		
		using (var fileStream = File.OpenRead(podcastsJson))
		{
			var tempPodcasts = JsonSerializer.Deserialize<List<Podcast>>(fileStream);
			if (tempPodcasts != null)
			{
				podcasts.AddRange(tempPodcasts);
			}
		}

		if (podcasts.Count == 0)
		{
			Log.Error("No podcasts found, exiting.");
			return 0;
		}

		// Go through each podcast.
		foreach (var podcast in podcasts)
		{
			Log.Information($"Loading podcast: {podcast.Name}");
			if (String.IsNullOrEmpty(podcast.Url))
			{
				Log.Information("No url set, skipping.");
				continue;
			}
			
			// Create the podcaste storage if it does not already exist
			var podcastPath = Path.Combine(archivePath, podcast.Name);

			if (Directory.Exists(podcastPath) == false)
			{
				try
				{
					Directory.CreateDirectory(podcastPath);
				}
				catch (Exception err)
				{
					Log.Error(err, "Could not create podcast path.");
					return 1;
				}
			}
			
			// Download podcast manifest.
			var podcastResponse = await _httpClient.GetAsync(podcast.Url);
			if (podcastResponse.StatusCode != HttpStatusCode.OK)
			{
				Log.Error("Unable to download podcast RSS feed.");
				return 1;
			}
			
			
			// Save this data to podcast.xml
			var podcastXmlPath = Path.Combine(podcastPath, "podcast.xml");
			try
			{
				// If the podcast XML file exists back it up.
				if (File.Exists(podcastXmlPath) == true)
				{
					var backupPath = Path.Combine(podcastPath, $"podcast_backup_{DateTimeOffset.Now.ToUnixTimeSeconds()}.xml");
					File.Move(podcastXmlPath, backupPath);
				}
				var podcastData = await podcastResponse.Content.ReadAsStringAsync();
				File.WriteAllText(podcastXmlPath, podcastData);
			}
			catch (Exception err)
			{
				Log.Error(err, "Unable to backup or download latest podcast RSS feed.");
				return 1;
			}
			
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
					Log.Information("Downloading cover");
					using (var stream = await _httpClient.GetStreamAsync(coverUri.AbsoluteUri))
					{
						using (var fileStream = File.Create(coverPath))
						{
							await stream.CopyToAsync(fileStream);
						}
					}
				}
				else
				{
					Log.Information("Cover already exists, skipping");
				}

				// The cover URL has resizing attributes on it as well, so lets get the original url without attributes and save that.
				if (coverUri.AbsoluteUri.Contains('?') == true)
				{
					var coverOriginalPath = Path.Combine(podcastPath, $"cover_original{coverExtension}");
					if (File.Exists(coverOriginalPath) == false)
					{
						Log.Information("Downloading original cover");
						using (var stream = await _httpClient.GetStreamAsync($"https://{coverUri.Host}{coverUri.AbsolutePath}"))
						{
							using (var fileStream = File.Create(coverOriginalPath))
							{
								await stream.CopyToAsync(fileStream);
							}
						}
					}
					else
					{
						Log.Information("Original cover already exists, skipping");
					}
				}
				else
				{
					Log.Information("Cover is not modified, so original cover is not needed.");
				}
			}
			else
			{
				Log.Error("Could not get podcast cover images.");
			}

			// Now to get each episode and download those.
			var itemNodes = xmlDoc.DocumentElement?.SelectNodes("/rss/channel/item") as XmlNodeList;
			if (itemNodes == null)
			{
				Log.Information("Error: No podcast episodes found.");
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
				var title = item["title"]?.InnerText.Trim();
				if (String.IsNullOrEmpty(title) == true)
				{
					Log.Error("Title is empty.");
					continue;
				}

				var guid = item["guid"]?.InnerText?.ToLower();
				if (String.IsNullOrEmpty(guid) == true)
				{
					Log.Error("Guid is empty.");
					continue;
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
				catch (Exception err)
				{
					Log.Error(err, "Error getting itunes:episode node.");
				}

				try
				{
					var seasonText = item["itunes:season"]?.InnerText;
					if (String.IsNullOrEmpty(seasonText) == false)
					{
						seasonInt = Int32.Parse(seasonText);
					}
				}
				catch (Exception err)
				{
					Log.Error(err, "Error getting itunes:season node.");
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
					Log.Error("pubDate not found.");
					continue;
				}
				
				var pubDate = DateTime.Parse(pubDateString).ToUniversalTime();
				var enclosure = item.SelectSingleNode("enclosure");
				if (enclosure == null)
				{
					Log.Error("Enclosure not found.");
					continue;
				}

				// Enclosure tag is what contains the actual url.
				var enclosureUriString = enclosure.Attributes?.GetNamedItem("url")?.Value;
				if (String.IsNullOrEmpty(enclosureUriString) == true)
				{
					Log.Error("Enclosure url not found.");
					continue;
				}

				var enclosureUri = new Uri(enclosureUriString);

				// Now we create the episode filename, this could be something like,
				// 2021-03-27 - S4 E28 Ha, that good one (b8a112d6-4b8b-42f0-9389-d7d22419dc5f).mp3
				// 2021-03-28 - E271 Yep that was good to (b8a112d6-4b8b-42f0-9389-d7d22419dc5f).mp3
				// 2021-03-29 - That one where we all cried (b8a112d6-4b8b-42f0-9389-d7d22419dc5f).mp3
				var enclosureExtension = Path.GetExtension(enclosureUri.AbsolutePath);
				var episodeFilenameOld = MakeSafeFilenameOld($"{pubDate:yyyy-MM-dd} - {episodeSeasonPrefix}{title}{enclosureExtension}", '-');
				var episodeFilename = MakeSafeFilenameNew($"{pubDate:yyyy-MM-dd} - {episodeSeasonPrefix}{title} ({guid}){enclosureExtension}");


				// This is the final path of where we save it.
				var episodePathOld = Path.Combine(podcastPath, episodeFilenameOld);
				var episodePath = Path.Combine(podcastPath, episodeFilename);

				if (File.Exists(episodePathOld) == true)
				{
					Log.Information("Moving podcast from old format to new format");
					Log.Information($"Old: {episodePathOld}");
					Log.Information($"New: {episodePath}");
					File.Move(episodePathOld, episodePath);
				}
				
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
					//Log.Information($"File already exists, skipping, {episodeFilename}");
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
				Log.Information($"Downloading {episodeFilename}");

				try
				{
					// Use this stream to download the data.
					using (var stream = await _httpClient.GetStreamAsync(fileSummary.RemoteUrl))
					{
						using (var fileStream = File.Create(fileSummary.LocalFilename))
						{
							await stream.CopyToAsync(fileStream);
							fileSummary.ActualLength = fileStream.Length;
						}
					}
				}
				catch (Exception err)
				{
					Log.Information(err, "Could not download podcast.");
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
		
		return 0;
	}
}

