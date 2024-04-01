using System;
using System.CommandLine;
using System.CommandLine.Invocation;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Serilog;
using Serilog.Core;
using SQLite;

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
	
	static string MakeSafeFilename(string filename)
	{
		var originalFilename = new string(filename);
		
		// Special cases for podcasts that "use a format // like this"
		filename = filename.Replace("//", "-");
		filename = filename.Replace(" w/ ", " with ");
		filename = filename.Replace(" / ", ", ");
		filename = filename.Replace("/", String.Empty);
		filename = filename.Replace("\\", String.Empty);
		filename = filename.Replace(": ", " - ");
		filename = filename.Replace(";", " - ");
		filename = filename.Replace("…", "...");
		filename = filename.Replace("“", "\"");
		filename = filename.Replace("”", "\"");
		filename = filename.Replace("‘", "'");
		filename = filename.Replace("ʻ", "'");
		filename = filename.Replace(" ", " ");
		filename = filename.Replace("\ufeff", " ");
		filename = filename.Replace("ʻ", "'");
		filename = filename.Replace(":", String.Empty);
		filename = filename.Replace("*", String.Empty);
		filename = filename.Replace("?", String.Empty);
		filename = filename.Replace("\"", "'");
		filename = filename.Replace("<", "[");
		filename = filename.Replace(">", "]");
		filename = filename.Replace(" | ", ", ");
		filename = filename.Replace("|", String.Empty);
		filename = filename.Replace("’", "'");
		filename = filename.Replace("–", "-");
		//filename = filename.Replace("#", String.Empty); // handled this better at the title level.
		filename = filename.Replace("%", "percent"); // only appears once in the RT podcast archive.

		if (originalFilename != filename)
		{
			//Log.Information($"\n\nXX OLD: {originalFilename}\nXX NEW: {filename}\n\n");
		}
		//var cleanFileName = Regex.Replace(filename, "[^a-zA-Z0-9._\\-() ]+", String.Empty);
		
		return filename;
		/*
		
		var cleanFileName = Regex.Replace(filename, "[^a-zA-Z0-9._\\-() ]+", String.Empty);
		Log.Information($"{filename} -> {cleanFileName}");
		return cleanFileName;
		*/
	}

	static void BackupFile(string fileName)
	{
		if (File.Exists(fileName) == true)
		{
			var newFileName = $"{Path.GetFileNameWithoutExtension(fileName)}.backup.{DateTimeOffset.Now.ToUnixTimeSeconds()}{Path.GetExtension(fileName)}";
			var backupPath = Path.Combine(Path.GetDirectoryName(fileName) ?? String.Empty, newFileName);
			File.Move(fileName, backupPath);
		}
	}


	static async Task<int> RunAsync(string outputPath)
	{
		#if DEBUG
		var loadPodcastsFromCache = true;
		#else
		var loadPodcastsFromCache = false;
		#endif
		
		_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4.1 Safari/605.1.15");
			
		Log.Information($"Using output path: {outputPath}");
		
		
		var tempDownloadsDirectory = Path.Combine(Path.GetTempPath(), "rt_podcast_archiver_downloads");
		Log.Information($"Using temp downloads path: {tempDownloadsDirectory}");
		
		if (Directory.Exists(tempDownloadsDirectory))
		{
			var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D"));

			try
			{
				Directory.Move(tempDownloadsDirectory, tempPath);
				Directory.Delete(tempDownloadsDirectory);
			}
			catch (Exception err)
			{
				Log.Error(err, "Could not remove old temp data.");
				return 1;
			}
		}

		try
		{
			Directory.CreateDirectory(tempDownloadsDirectory);
		}
		catch (Exception err)
		{
			Log.Error(err, "Could not create new temp directory");
			return 1;
		}
		

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
			Log.Error($"ERROR: Could not create output paths. ({err.Message})");
			return 1;
		}
		
		// Get an absolute path to the database file
		var databasePath = Path.Combine(archivePath, "database.db");

		var db = new SQLiteConnection(databasePath);
		db.CreateTable<PodcastEpisode>();

		// Setup logger
		Log.Logger = new LoggerConfiguration()
			.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss}] {Message}{NewLine}{Exception}")
			.WriteTo.File(Path.Combine(logPath, "rt_podcast_archiver_.log"), rollingInterval: RollingInterval.Day)
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
			
			podcasts.Add(new Podcast("30 Morbid Minutes (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("A Simple Talk (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Always Open (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("ANMA (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Annual Pass (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Beneath (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Black Box Down (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("D&D, but... (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("DEATH BATTLE Cast (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("F**kface (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Face Jam (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Funhaus Podcast (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Good Morning From Hell (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Hypothetical Nonsense (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Must Be Dice (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Off Topic (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("OT3 Podcast (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Red Web (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Rooster Teeth Podcast (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Ship Hits The Fan (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("So... Alright (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Tales from the Stinky Dragon (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("The Dogbark Podcast (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("The Most (FIRST Member Ad-Free)"));
			podcasts.Add(new Podcast("Trash for Trash (FIRST Member Ad-Free)"));
			
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

		Log.Information("Starting sync");
		
		
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

		//podcasts.Sort((a, b) => a.Name.CompareTo(b.Name));
		
		var allFileNames = new List<string>();
		
		// Go through each podcast.
		foreach (var podcast in podcasts)
		{
			if (podcast.IsEnabled == false)
			{
				continue;
			}
			
			if (loadPodcastsFromCache == false)
			{
				await Task.Delay(500);
			}
			

			Log.Information($"Loading podcast: {podcast.Name}");
			if (String.IsNullOrEmpty(podcast.Url))
			{
				Log.Information("No url set, skipping.");
				continue;
			}
			
			// Create the podcast storage if it does not already exist
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
			
			// We will save the podcast data to this file
			var podcastXmlPath = Path.Combine(podcastPath, "podcast.xml");
			
			if (loadPodcastsFromCache == true && File.Exists(podcastXmlPath))
			{
				Log.Information($"Using cache. Not loading fresh fresh rss stream ({podcast.Url})");
			}
			else
			{
				// Download podcast manifest.
				Log.Information($"Downloading fresh rss stream - {podcast.Url}");
				var podcastResponse = await _httpClient.GetAsync(podcast.Url);
				if (podcastResponse.StatusCode != HttpStatusCode.OK)
				{
					Log.Error("Unable to download podcast RSS feed.");
					return 1;
				}
			
				try
				{
					// If the podcast XML file exists back it up.
					BackupFile(podcastXmlPath);
					var podcastData = await podcastResponse.Content.ReadAsStringAsync();
					File.WriteAllText(podcastXmlPath, podcastData);
				}
				catch (Exception err)
				{
					Log.Error(err, "Unable to backup or download latest podcast RSS feed.");
					return 1;
				}
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

			#if DEBUG
			items = items.Take(10).ToList();
			#endif
			
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

				title = title.Replace("–", "-");
				title = title.Replace("\ufeff", " ");
				
				// Remove all double spaces.
				do
				{
					title = title.Replace("  ", " ");
				} while (title.Contains("  "));

				var guid = item["guid"]?.InnerText?.ToLower();
				if (String.IsNullOrEmpty(guid) == true)
				{
					Log.Error("Guid is empty.");
					continue;
				}

				// Sometimes guid is a number, so thats great -_-
				// We prefer this format so all files are named appropriately,
				// so we convert these ints to guids.
				if (guid.Length != 36)
				{
					if (int.TryParse(guid, out int guidInt) == true)
					{
						var guidBytes = Guid.Empty.ToByteArray();
						var valueBytes = BitConverter.GetBytes(guidInt);
						Array.Copy(valueBytes, 0, guidBytes, guidBytes.Length - valueBytes.Length, valueBytes.Length);
						var newGuid = new Guid(guidBytes);
						guid = newGuid.ToString("D").ToLower();
					}
					else
					{
						Log.Error($"Invalid guid for \"{title}\", could not convert from int. ({guid})");
						continue;
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


				#region Manual episode fixes

				if (podcast.Name == "30 Morbid Minutes (FIRST Member Ad-Free)" || podcast.Name == "30 Morbid Minutes")
				{
					if (guid == "49392c9a-e5ff-11ed-9fdc-13177201a8ae" ||
					    guid == "36670d4e-e5ff-11ed-92d6-373dc3311900")
					{
						// 2023-05-01 0700 - S05 E142 - The Screaming Mummy (49392c9a-e5ff-11ed-9fdc-13177201a8ae).mp3
						episodeInt = 42;
					}
					else if (guid == "68d2f436-a5ae-11ee-ba8e-0bea39a3a468" ||
					         guid == "8828dc38-a5ae-11ee-a1d2-5fbde4d96254")
					{
						// 2024-01-02 0800 - E69 - Inside Body Farms with a Future Resident (68d2f436-a5ae-11ee-ba8e-0bea39a3a468).mp3
						seasonInt = 8;
					}
				}
				else if (podcast.Name == "A Simple Talk (FIRST Member Ad-Free)")
				{
					if (guid == "f998b656-58ae-11ee-86ef-cf937693f33c")
					{
						// 2023-09-21 1842 - Simple Talk - We Missed Our Plane! (f998b656-58ae-11ee-86ef-cf937693f33c).mp3
						episodeInt = 1;
					}
					else if (guid == "643fc958-62ce-11ee-9c97-43795ce081d7")
					{
						// 2023-10-05 1501 - A Simple Talk - Tent Talk & Monkeys (643fc958-62ce-11ee-9c97-43795ce081d7).mp3
						episodeInt = 4;
					}
					else if (guid == "259e14a8-6930-11ee-9b4d-3b2bc23a0cd0")
					{
						// 2023-10-12 1859 - A Simple Talk - Dodging Cars and Cows (259e14a8-6930-11ee-9b4d-3b2bc23a0cd0).mp3
						episodeInt = 5;
					}
					else if (guid == "42266922-6930-11ee-936e-8fb40bad8bb7")
					{
						// 2023-10-12 1900 - A Simple Talk - The Return of the Tent Talk (42266922-6930-11ee-936e-8fb40bad8bb7).mp3
						episodeInt = 6;
					}
					else if (guid == "04d96a80-6dca-11ee-b501-0b52c9ab3e05")
					{
						// 2023-10-19 1501 - A Simple Talk - Kerry Gets Caught (04d96a80-6dca-11ee-b501-0b52c9ab3e05).mp3
						episodeInt = 7;
					}
					else if (guid == "1f551238-6dca-11ee-992c-d38db5e93e62")
					{
						// 2023-10-19 1501 - A Simple Talk - Our Last Day (1f551238-6dca-11ee-992c-d38db5e93e62).mp3
						episodeInt = 8;
					}
				}
				else if (podcast.Name == "Always Open")
				{
					if (guid == "a6b38f04-16de-11ee-9c3f-8fc0dfba7d08")
					{
						// 2023-07-11 130000 - E19 - Does Exercise ACTUALLY help your Brain (a6b38f04-16de-11ee-9c3f-8fc0dfba7d08).mp3
						seasonInt = 7;
						episodeInt = 159;
					}

					var alwaysOpenRegex = new Regex(@"^(.*)-(\s*)#(?<episodeNumber>\d+)$");
					var match = alwaysOpenRegex.Match(title);
					if (match.Success)
					{
						if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
						{
							episodeInt = tempEpisodeInt;
						}
					}
					
					//Debugger.Break();
				}
				else if (podcast.Name == "Always Open (FIRST Member Ad-Free)")
				{
					if (guid == "b9903262-16de-11ee-9d10-23c3320b495e")
					{
						// 2023-07-10 1300 - E19 - Does Exercise ACTUALLY help your Brain - Always Open (b9903262-16de-11ee-9d10-23c3320b495e).mp3
						seasonInt = 7;
						episodeInt = 159;
					}
				}
				else if (podcast.Name == "ANMA (FIRST Member Ad-Free)" || podcast.Name == "ANMA")
				{
					// Unsure what should be done to these episodes, so they are left as is.
					// 2022-06-19 0700 - E7 - The Beginning of Our Internet Journey (7346c710-ee64-11ec-b323-7b2bc1fab113).mp3
					// 2022-06-26 0700 - E2 - Convention Memories (41443606-f406-11ec-bc8e-ffe149b2963c).mp3
					// 2022-07-03 0700 - E1 - Geoff & Eric at Vidcon (b28ff310-f891-11ec-bea6-537e621e57de).mp3
					// 2022-07-10 0700 - E2 - LEAKED ANMA RTX Panel Live (11be2d24-ff0a-11ec-b67d-bb90bacbeb66).mp3
					// 2022-07-17 0700 - E9 - Gus Gets Puked On (0d0b9b12-0532-11ed-98d9-abd4c691f5bb).mp3
					if (guid == "32899d96-c1fe-11ee-b94e-cf2d0af2da17" ||
					    guid == "37b9d650-c1fe-11ee-b4d5-4b8016f6d4f7")
					{
						// This is actually a supplemental episode
						// 2024-02-05 0800 - E74 - Geoff & Eric Talk (More) Music (32899d96-c1fe-11ee-b94e-cf2d0af2da17).mp3
						episodeInt = -1;
					}

					// S03??
					// 2024-02-19 0800 - E74 - Average President's Day (8fe6d59a-cd15-11ee-82fe-ab1e1885e9d3).mp3
					// 2024-02-26 0800 - S03 E75 - Third Wave Coffee (94f93eba-d288-11ee-941f-dfda8057d242).mp3
					// 2024-03-04 0800 - E76 - Paradise for Gentlemen (7eebe7d6-d813-11ee-b864-938054e0095f).mp3
					// 2024-03-11 0700 - S03 E77 - It's a Mayonnaise Commercial (ce2cb668-dd77-11ee-987d-c7d840b52aa7).mp3
					// 2024-03-18 0700 - E78 - The Future of ANMA (6465f68c-e302-11ee-a065-874f56b5dc97).mp3
					if (guid == "94f93eba-d288-11ee-941f-dfda8057d242" || 
					    guid == "9abff58c-d288-11ee-b417-e70160b0e254")
					{
						// 2024-02-26 0800 - S03 E75 - Third Wave Coffee (94f93eba-d288-11ee-941f-dfda8057d242).mp3
						seasonInt = -1;
					}
					else if (guid == "ce2cb668-dd77-11ee-987d-c7d840b52aa7" ||
					         guid == "d84a009c-dd77-11ee-8a65-bb990b0d3a3a")
					{
						// 2024-03-11 0700 - S03 E77 - It's a Mayonnaise Commercial (ce2cb668-dd77-11ee-987d-c7d840b52aa7).mp3
						seasonInt = -1;
					}
					else if (guid == "61fb6918-e87a-11ee-b4cb-a769e3c22fab" || 
					         guid == "647837a2-e87a-11ee-ba95-23b76ad25d35")
					{
						// 2024-03-25 070000 - S03 E79 - In the Carcass of Our Memories (61fb6918-e87a-11ee-b4cb-a769e3c22fab).mp3
						seasonInt = -1;
					}
					else if (guid == "41443606-f406-11ec-bc8e-ffe149b2963c")
					{
						// 2022-06-26 070000 - E2 - Convention Memories (41443606-f406-11ec-bc8e-ffe149b2963c).mp3
						episodeInt = 8;
					}
					else if (guid == "b28ff310-f891-11ec-bea6-537e621e57de")
					{
						// 2022-07-03 070000 - E1 - Geoff & Eric at Vidcon (b28ff310-f891-11ec-bea6-537e621e57de).mp3
						episodeInt = -1;
					}
					else if (guid == "11be2d24-ff0a-11ec-b67d-bb90bacbeb66")
					{
						// 2022-07-10 070000 - E2 - LEAKED ANMA RTX Panel Live (11be2d24-ff0a-11ec-b67d-bb90bacbeb66).mp3
						episodeInt = -1;
					}
				}
				else if (podcast.Name == "Annual Pass (FIRST Member Ad-Free)" || podcast.Name == "Annual Pass")
				{
					// Perfect, no notes.
				}
				else if (podcast.Name == "Annual Pass")
				{
					if (guid == "28c2777a-d09b-11ec-aa9c-936ef46a490a")
					{
						// 2022-05-12 070000 - An Interview with Carme - Disney Fireworks Specialist (28c2777a-d09b-11ec-aa9c-936ef46a490a).mp3
						episodeInt = 56;
					}
				}
				else if (podcast.Name == "Beneath (FIRST Member Ad-Free)" || podcast.Name == "Beneath")
				{
					// Perfect, no notes.
				}
				else if (podcast.Name == "Black Box Down (FIRST Member Ad-Free)")
				{
					// 
					if (guid == "4877385c-33cf-11ed-9131-6f189628cf2b")
					{
						// 2022-09-14 0700 - E91 - Seaplane Tragedy in Miami, Chalk's Ocean Airways Flight 101 (4877385c-33cf-11ed-9131-6f189628cf2b).mp3
						seasonInt = 2;
					}
					else if (guid == "c25420a4-3960-11ed-9939-975426a161ed")
					{
						// 2022-09-23 1623 - E92 - Air Moorea 1121, A Seven Minute Flight Ends in Tragedy (c25420a4-3960-11ed-9939-975426a161ed).mp3
						seasonInt = 2;
					}
					else if (guid == "7d2f82e0-12ad-11ed-b5d8-cb167f08ab4c")
					{
						// 2022-08-03 070000 - S09 E88 - Our Scariest Airplane Moments!, First Class (7d2f82e0-12ad-11ed-b5d8-cb167f08ab4c).mp3
						episodeInt = -1;
					}
					else if (guid == "8c14eedc-c2aa-11ed-a563-bf9f1c1c047a")
					{
						// 2023-03-15 070000 - Near Misses and Aviation Headline Updates, First Class (8c14eedc-c2aa-11ed-a563-bf9f1c1c047a).mp3
						seasonInt = 11;
					}
					else if (guid == "9a3605dc-c831-11ed-a120-a7b91c2c14d0")
					{
						// 2023-03-22 070000 - Picking Apart Planes in Movies with Blaine from 'Tales from the Stinky Dragon' (9a3605dc-c831-11ed-a120-a7b91c2c14d0).mp3
						seasonInt = 11;
					}
					else if (guid == "ae0b85d4-7587-11ed-b5d8-e39f9d4ebe50")
					{
						// 2022-12-07 080000 - A Plane Gets Stuck in an Electrical Tower & Other Current Events, First Class (ae0b85d4-7587-11ed-b5d8-e39f9d4ebe50).mp3
						seasonInt = 10;
					}
					else if (guid == "62c3adc6-d55b-11ec-a86b-9777fb213db6")
					{
						// 2022-05-18 070000 - S08 E81 - All About Airspaces, The Places You Can't Fly In (62c3adc6-d55b-11ec-a86b-9777fb213db6).mp3
						episodeInt = -1;
						
						// The following few are to fix this


						// 2022-05-18 0700 - S08 E81 - All About Airspaces, The Places You Can't Fly In (62c3adc6-d55b-11ec-a86b-9777fb213db6).mp3
						// 2022-06-01 0700 - S08 E82 - Leaving Luggage in an Airport feat. Geoff Ramsey, Chris Left His Pants on a Plane (08c2be3c-e124-11ec-a968-4f01a79d1415).mp3
						// 2022-06-08 0700 - S09 E83 - Was This Crash an Assassination, Polish Air Force Flight 101 Crashes with President on Board (0ef203be-e67c-11ec-8aef-b77fc2d6046c).mp3
						// 2022-06-15 0700 - S09 E82 - Pilots Struggle as Airplane Nosedives, Alaska Airlines Flight 261 Crashes off California Coast (4f804628-ec23-11ec-8be6-3381e2e6d57d).mp3
						// 2022-06-22 0700 - S09 E83 - Pilots Accidently Cause a Go Around, China Airlines Flight 140 Crashes At Airport (38443122-f1b9-11ec-b72e-5f43ca3dec9c).mp3
						// 2022-06-29 0700 - S09 E84 - Airplane Does a Barrel Roll with Passengers on Board, American Eagle Flight 4184 Loses Control (3b72abd8-f724-11ec-a0f0-e3999ce6abe8).mp3
						// 2022-07-06 0700 - S09 E85 - Did The Pilot Crash This Plane on Purpose, EgyptAir Flight 990 Ends In Controversy (772183da-fcc3-11ec-8e0a-7f50c8d3e3a5).mp3
					}
					else if (guid == "0ef203be-e67c-11ec-8aef-b77fc2d6046c")
					{
						// 2022-06-08 070000 - S09 E83 - Was This Crash an Assassination, Polish Air Force Flight 101 Crashes with President on Board (0ef203be-e67c-11ec-8aef-b77fc2d6046c).mp3
						episodeInt = 81;
					}
					else if (guid == "08c2be3c-e124-11ec-a968-4f01a79d1415")
					{
						// 2022-06-01 070000 - S08 E82 - Leaving Luggage in an Airport feat. Geoff Ramsey, Chris Left His Pants on a Plane (08c2be3c-e124-11ec-a968-4f01a79d1415).mp3
						episodeInt = -1;
					}
				}
				else if (podcast.Name == "D&D, but... (FIRST Member Ad-Free)" || podcast.Name == "D&D, but...")
				{
					// Perfect, no notes.
				}
				else if (podcast.Name == "DEATH BATTLE Cast (FIRST Member Ad-Free)" || podcast.Name == "DEATH BATTLE Cast")
				{
					// Looking at title for episode number, this says #80, but its the 60th in the list
					// 2018-06-09 1900 - Strange vs Fate Sneak Peak - #80 (00000000-0000-0000-0000-000050000000).mp3

					// Missing episode 243, 
					// Turns out this exists in the non-first podcast streams.
					// 2021-08-20 1700 - E241 - Gru VS Megamind (31ecfa82-012a-11ec-85da-3b7ac45d7395).mp3
					// 2021-08-27 1700 - E242 - Osmosis Jones VS White Blood Cell U-1146 (cd59d882-068e-11ec-baf6-6fe9a1d72f4b).mp3
					// 2021-09-16 1700 - E245 - Emperor Joker VS God Kefka (4364efde-16f8-11ec-a28e-d33484d4949e).mp3
					// 2021-09-23 1700 - E246 - Wario VS Knuckles (eadaf0d2-1c2b-11ec-a94c-b745395eff61).mp3

					// We started getting S08
					if (guid == "0b5dd28c-bddb-11ed-86b3-8ba8aa384f5c" || 
					    guid == "0cc477de-bddb-11ed-ae20-af205251417d")
					{
						// 2023-03-09 1800 - E321 - Tier Ranking ULTIMATE Female Villains (0b5dd28c-bddb-11ed-86b3-8ba8aa384f5c).mp3
						seasonInt = 8;
					}
					else if (guid == "9ab71e6c-c35c-11ed-b8ad-b7e86d44988a" ||
					         guid == "9b6505ae-c35c-11ed-bf8f-b3fa6e2df4c4")
					{
						// 2023-03-16 1700 - E322 - Deku VS Gon (9ab71e6c-c35c-11ed-b8ad-b7e86d44988a).mp3
						seasonInt = 8;
					}
					else if (guid == "129062f8-c8f9-11ed-962a-8f5b56686fc8" ||
					         guid == "1352b6fa-c8f9-11ed-bc0b-576e5564f6bf")
					{
						// 2023-03-23 1700 - E323 - Nemesis VS Pyramid Head (129062f8-c8f9-11ed-962a-8f5b56686fc8).mp3
						seasonInt = 8;
					}
					else if (guid == "ac1a70be-1b8b-11ee-88ef-d7d5640237f0" ||
					         guid == "b371fbfc-1b8b-11ee-8a27-2b684d6a2469")
					{
						// 2023-07-06 1700 - E338 - Captain Britain (Marvel) vs Uncle Sam (DC) (ac1a70be-1b8b-11ee-88ef-d7d5640237f0).mp3
						seasonInt = 8;
					}
				}
				else if (podcast.Name == "F**kface (FIRST Member Ad-Free)" || podcast.Name == "F**kface")
				{
					if (guid == "5a763164-9088-11ed-a11b-9372f9497016" ||
					    guid == "5b6f5dac-9088-11ed-926f-5731c0652918")
					{
						// 2023-01-10 0800 - E136 - Andrew is On Your Side - Geoff's year old Cosmic Crisp (5a763164-9088-11ed-a11b-9372f9497016).mp3
						seasonInt = 5;
					}
					else if (guid == "a07ecb2e-572c-11ee-81a6-b765bc79ff5f" || 
					         guid == "e0f373f8-572c-11ee-af08-27e4b6f9c325")
					{
						// 2023-09-20 0700 - E172 - Cock Money - Punchlines (a07ecb2e-572c-11ee-81a6-b765bc79ff5f).mp3
						seasonInt = 6;
					}
					else if (guid == "e6b5be60-5c93-11ee-b21e-c77334f025aa" ||
					         guid == "e910a8d2-5c93-11ee-bfe4-977b96e6151e")
					{
						// 2023-09-27 0700 - E173 - Andrews Ankles - Regulation Flavors (e6b5be60-5c93-11ee-b21e-c77334f025aa).mp3
						seasonInt = 6;
					}
					else if (guid == "670e1858-620e-11ee-b5c5-a3ef96ca8d9d" ||
					         guid == "68679300-620e-11ee-a48f-bf28619703d4")
					{
						// 2023-10-04 0700 - E174 - Caviar Phones - Internal Monologues (670e1858-620e-11ee-b5c5-a3ef96ca8d9d).mp3
						seasonInt = 6;
					}
					else if (guid == "03d6e61c-679e-11ee-a7ba-0fbfecebf62d" ||
					         guid == "04777852-679e-11ee-9fe8-db6e630a3836")
					{
						// 2023-10-11 0700 - E175 - Baby Alien Schlongs - Sleep Hacks (03d6e61c-679e-11ee-a7ba-0fbfecebf62d).mp3
						seasonInt = 6;
					}
					else if (guid == "19e1229e-6d19-11ee-bd21-f79bbe91ead3" ||
					         guid == "182e2294-6d19-11ee-9b09-bf823737c5bc")
					{
						// 2023-10-18 0700 - E176 - Tomorrow Is Chores - Naughty Naked Video Games  (19e1229e-6d19-11ee-bd21-f79bbe91ead3).mp3
						seasonInt = 6;
					}
					else if (guid == "d0deef90-729a-11ee-aff4-83b381d8c635" ||
					         guid == "ceab9b4c-729a-11ee-89db-13851b0622c4")
					{
						// 2023-10-25 0700 - E177 - Appropriate Squirts - Key West Bachelorette Weekend (d0deef90-729a-11ee-aff4-83b381d8c635).mp3
						seasonInt = 6;
					}
					else if (guid == "93f7faec-9ebc-11ee-b0ab-47000b1f6dca" ||
					         guid == "995f3ea0-9ebc-11ee-ad58-07c43226c3b1")
					{
						// 2023-12-20 0800 - E185 - It's Scary Out There - Wheel of Years (93f7faec-9ebc-11ee-b0ab-47000b1f6dca).mp3
						seasonInt = 6;
					}
					else if (guid == "73e79d30-9ec5-11ee-b6d7-639b5cea62fd" || 
					         guid == "787ea686-9ec5-11ee-9513-139283bb095e")
					{
						// 2023-12-27 0800 - E186 - Assholes and Ice Skates - Fart Drama (73e79d30-9ec5-11ee-b6d7-639b5cea62fd).mp3
						seasonInt = 6;
					}
					else if (guid == "7717ac22-a9b3-11ee-8a77-a7b28706223d" || 
					         guid == "798108c8-a9b3-11ee-9443-4b1af98d6e7c")
					{
						// 2024-01-03 0800 - E187 - Getting Our Dicks Wet In The New Year - Signs from Howard Stern (7717ac22-a9b3-11ee-8a77-a7b28706223d).mp3
						seasonInt = 6;
					}
					else if (guid == "b30cf3e6-c541-11ee-9167-67f3db2cf565" ||
					         guid == "e8a2c094-c541-11ee-af51-eb88f9a1c0b6")
					{
						// 024-02-07 0800 - E192 - In The Owl City Lab - Andrew Got A Cock (b30cf3e6-c541-11ee-9167-67f3db2cf565).mp3
						seasonInt = 6;
					}
					else if (guid == "badc4148-cabf-11ee-acb6-e7ccad1541a1" ||
					         guid == "bf4eb0a8-cabf-11ee-a0dd-7f9a4a9ec53d")
					{
						// 2024-02-14 0800 - E193 - Buying a Mini Blimp - Naked Floppy Running (badc4148-cabf-11ee-acb6-e7ccad1541a1).mp3
						seasonInt = 6;
					}
					else if (guid == "93169270-d03d-11ee-9fa6-6790a3e1de76" ||
					         guid == "950f04e0-d03d-11ee-a972-0bc0859fa7b3")
					{
						// 2024-02-21 0800 - E194 - Small Dick Mode - 8 Minute Tub Time (93169270-d03d-11ee-9fa6-6790a3e1de76).mp3
						seasonInt = 6;
					}
					else if (guid == "e20e8052-d5bc-11ee-bc84-43392ba46b40" ||
					         guid == "e3745a3e-d5bc-11ee-bcf6-5f337637fa48")
					{
						// 2024-02-28 0800 - E195 - Gavin is Here for Pleasantries - Season 2022 (e20e8052-d5bc-11ee-bc84-43392ba46b40).mp3
						seasonInt = 6;
					}
					else if (guid == "d6c1c52c-db3b-11ee-acde-ebc74a1073b5" ||
					         guid == "da7f3b40-db3b-11ee-b6ca-6794ba7153ed")
					{
						// 2024-03-06 0800 - E197 - Fidget Guns and Monster Trucks - Death of Umidigi (d6c1c52c-db3b-11ee-acde-ebc74a1073b5).mp3
						seasonInt = 6;
					}
					else if (guid == "11eee356-e63a-11ee-9b99-cbe1235ff40e" || 
					         guid == "16b28a00-e63a-11ee-ac95-df62f99e37e7")
					{
						// 2024-03-20 0700 - E199 - Discount Pranking - Farts In Written Word (11eee356-e63a-11ee-9b99-cbe1235ff40e).mp3
						seasonInt = 6;
					}
					else if (guid == "5917b87c-ebb5-11ee-b8d2-7fd3607e71a1" ||
					         guid == "620e7c5e-ebb5-11ee-8d47-cf5050ab39a9")
					{
						// Season 6 still?
						// 2024-03-27 070000 - E200 - Alabama Poutine - The Perpetual Food Truck (5917b87c-ebb5-11ee-b8d2-7fd3607e71a1).mp3
						seasonInt = 6;
					}
					else if (guid == "93fe1846-eb58-11ec-8f19-f7c3f51b402d")
					{
						//2022-06-15 070000 - E107 - Season 4, Year 3, Volume 1, Episode 107 - Future Us is as Lazy as Current We (93fe1846-eb58-11ec-8f19-f7c3f51b402d).mp3
						seasonInt = 4;
					}
					
					// Unsure if 139 is season 5 or season 6
					// 2023-01-24 0800 - S05 E138 - We Are 138 - 8 Hour Fireplace Video (d750e4d4-9b7a-11ed-938b-6bc8afc305a8).mp3
					// 2023-01-31 0800 - E139 - Are You Feeling Wronged - Silver Medal Friendship (24e307a2-a0f5-11ed-acc6-d371fda63fdc).mp3
					// 2023-02-07 0800 - S06 E140 - Nick's Laugh Track - Stitches SZN (04d5122e-a65a-11ed-b64d-7703949792b8).mp3

					// same name and season and episode as so... alright first episode
					// 2023-08-28 0700 - S01 E1 - So... Alright Premiere - Tough Times on Mango Street (d53583b2-437c-11ee-9011-63a19ee38ca5).mp3

					// Missing 196
					// 2024-02-28 0800 - E195 - Gavin is Here for Pleasantries - Season 2022 (e20e8052-d5bc-11ee-bc84-43392ba46b40).mp3
					// 2024-03-06 0800 - E197 - Fidget Guns and Monster Trucks - Death of Umidigi (d6c1c52c-db3b-11ee-acde-ebc74a1073b5).mp3

				}
				else if (podcast.Name == "Face Jam (FIRST Member Ad-Free)" || podcast.Name == "Face Jam")
				{
					// First numbered episode, is actually 38th episide
					// 2020-12-22 0800 - E30 - Taco Cabana Chicken Tinga & Cheese Poblano Torpedos (a751d6c0-4169-11eb-a11b-3f8929b86a68).mp3
					// Numbers are all over the shop, but dates look right so will leave those as is.

					// Unsure what is episode 55.
					// 2021-11-22 080000 - E54 - Applebees Cheetos Boneless Wings & Cheetos Cheese Bites (71be69a6-49d3-11ec-b6c8-cf21ea2f4ed9).mp3
					// 2021-11-24 180000 - Detroit Chicago Style Pizza in Pueblo Colorado (af46f504-4cb7-11ec-8a41-1b61c485b1b7).mp3
					// 2021-12-01 180000 - The Sandwich That Killed Elvis (eed05cba-5243-11ec-bff5-e3f47e785696).mp3
					// 2021-12-06 080000 - Outback Steakhouse Espresso Butter Steak (a141dde2-5488-11ec-9c14-2f340052ee81).mp3
					// 2021-12-20 080000 - E56 - Sonic Fritos Chili Cheese Wrap & Garlic Butter Bacon Burger (cf25aa24-5f94-11ec-97bd-43ac7e268f1e).mp3
					
					if (guid == "b1863dea-5783-11eb-9b78-47c1baa1c26f" ||
					    guid == "b1863dea-5783-11eb-9b78-47c1baa1c26f")
					{
						//2021-01-19 080000 - Dairy Queen Rotisserie-Style Chicken Bites & Brownie Dough Blizzard (b1863dea-5783-11eb-9b78-47c1baa1c26f).mp3
						episodeInt = 32;
					}
					else if (guid == "369843e2-6d88-11eb-b9aa-8ba38ba51dca" ||
					         guid == "369843e2-6d88-11eb-b9aa-8ba38ba51dca")
					{
						// 2021-02-16 080000 - Red Lobster Wagyu Bacon Cheeseburger (369843e2-6d88-11eb-b9aa-8ba38ba51dca).mp3
						episodeInt = 34;
					}
					else if (guid == "36096f46-9bf0-11eb-a233-0771cf9cf0f8" ||
					         guid == "36096f46-9bf0-11eb-a233-0771cf9cf0f8")
					{
						// 2021-04-13 070000 - TGI Fridays Under the Big Top Menu (36096f46-9bf0-11eb-a233-0771cf9cf0f8).mp3
						episodeInt = 38;
					}
					else if (guid == "458ea660-4690-11ed-b73d-776979bb74a2" ||
					         guid == "49ba393e-4690-11ed-b6d1-ab546c4a4057")
					{
						// 2022-10-10 070000 - Chili's Signature Bar Menu (458ea660-4690-11ed-b73d-776979bb74a2).mp3
						episodeInt = 77;
					}
					else if (guid == "5c405924-a140-11ee-92c4-d703c4298a13" ||
					         guid == "4b2d4d30-a141-11ee-b550-4b626fee443a")
					{
						// 2024-01-02 080000 - Chuck E Cheese Grown Up Menu (5c405924-a140-11ee-92c4-d703c4298a13).mp3
						episodeInt = 108;
					}
					else if (guid == "57bc256c-2dfc-11ed-87b4-47a32d663c1e" ||
					         guid == "0fe069d8-2dfc-11ed-b5e1-432a80d0165a")
					{
						// 2022-09-06 161000 - E1 - Spittin Silly - Theme Song (57bc256c-2dfc-11ed-87b4-47a32d663c1e).mp3
						episodeInt = -1;
					}
					else if (guid == "fe86efb8-ae47-11ee-b0da-d389f0120b00" ||
					         guid == "032414f6-ae48-11ee-bf80-cf437ebc56ea")
					{
						// 2024-01-09 080000 - E36 - Spittin Silly - Freewheelin (fe86efb8-ae47-11ee-b0da-d389f0120b00).mp3
						episodeInt = -1;
					}
					else if (guid == "784aa38c-66bb-11ee-a410-8b65cac3ade1" ||
					         guid == "78fe60ac-66bb-11ee-a665-dbca7df3ef20")
					{
						// 2023-10-10 070000 - Fazoli's Pizza Baked Pasta (784aa38c-66bb-11ee-a410-8b65cac3ade1).mp3
						episodeInt = 102;
					}

				}
				else if (podcast.Name == "Funhaus Podcast (FIRST Member Ad-Free)" || podcast.Name == "Funhaus Podcast")
				{
					// No episode 23
					// 2015-07-01 1443 - Are We GAMES JOURNALISTS  - #22 (00000000-0000-0000-0000-000016000000).mp3
					// 2015-07-15 1349 - Batman v Superman - WILL IT SUCK - #24 (00000000-0000-0000-0000-000018000000).mp3

					// No episode 30
					// 2015-08-25 1940 - Nintendo NX Details - #29 (00000000-0000-0000-0000-00001d000000).mp3
					// 2015-09-02 1504 - Should You Play Metal Gear Solid 5 - #31 (00000000-0000-0000-0000-00001e000000).mp3

					// No episode 208
					// 2019-01-02 1400 - 2019 is Gonna Be Awesome! - Dude Soup Podcast #207 (00000000-0000-0000-0000-0000cf000000).mp3
					// 2019-01-16 2200 - YouTube is Forcing Us to Change Our Content - Dude Soup Podcast #209 (00000000-0000-0000-0000-0000d1000000).mp3

					// No 251
					
					// #282 is teh last postfix numbered episode for a while

					// Then we drop into actual season numbers.
					if (guid == "e6ca8d3e-d0d7-11ec-a68b-6b72a6b5e8ba")
					{
						// 2022-05-11 1300 - S2022 E19 - Comedy is Not a Full Contact Sport - Funhaus Podcast (e6ca8d3e-d0d7-11ec-a68b-6b72a6b5e8ba).mp3
						seasonInt = 8;
					}
					else if (guid == "19da3794-dbda-11ec-bc2a-0760d828da92")
					{
						// 2022-05-25 1300 - S2022 E21 - No More Binging on Bargain Shrimp - Funhaus Podcast (19da3794-dbda-11ec-bc2a-0760d828da92).mp3
						seasonInt = 8;
					}
					else if (guid == "64c47f50-ec22-11ec-8334-d3cb58f76a09")
					{
						// 2022-06-15 1300 - All the Best NEW Upcoming Games from Bethesda, XBOX, and More! - Funhaus Podcast (64c47f50-ec22-11ec-8334-d3cb58f76a09).mp3
						seasonInt = 8;
						episodeInt = 24;
					}
					else if (guid == "4911d226-15d9-11ee-8eac-dbb9e25c36c8")
					{
						// 2023-06-28 2100 - S9435 - Barbie Meets True Crime in Our Latest Tabletop RPG - Funhaus Podcast (4911d226-15d9-11ee-8eac-dbb9e25c36c8).mp3
						seasonInt = 9;
						episodeInt = 435;
					}


					if (episodeInt == -1)
					{
						var funhausPodcastRegex = new Regex(@"^(.*)-(\s*)#(?<episodeNumber>\d+)$");
						var match = funhausPodcastRegex.Match(title);
						if (match.Success)
						{
							if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
							{
								episodeInt = tempEpisodeInt;
							}
						}
					}
					
					
					if (episodeInt == -1)
					{
						var funhausPodcastRegex = new Regex(@"^(.*)(\s*)#(\s*)(?<episodeNumber>\d+)$");
						var match = funhausPodcastRegex.Match(title);
						if (match.Success)
						{
							if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
							{
								episodeInt = tempEpisodeInt;
							}
						}
					}
					
					
					
					if (episodeInt == -1)
					{
						var funhausPodcastRegex = new Regex(@"^(.*) (?<episodeNumber>\d+)$");
						var match = funhausPodcastRegex.Match(title);
						if (match.Success)
						{
							if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
							{
								episodeInt = tempEpisodeInt;
							}
						}
					}

					if (guid == "00000000-0000-0000-0000-0000ef000000")
					{
						// 2019-08-14 210000 - Twitch Owns Ninja's Fans - Dude Soup Podcast (00000000-0000-0000-0000-0000ef000000).mp3
						episodeInt = 239;
					}
					else if (guid == "00000000-0000-0000-0000-0000f0000000")
					{
						// 2019-08-21 210000 - Apex Legends Picking Fights With Gamers - Dude Soup Podcast (00000000-0000-0000-0000-0000f0000000).mp3
						episodeInt = 240;
					}
					else if (guid == "00000000-0000-0000-0000-0000f1000000")
					{
						// 2019-08-28 210000 - Is the Disney+ Catalog Worthy Enough to Lift Thor's Hammer - Dude Soup Podcast (00000000-0000-0000-0000-0000f1000000).mp3
						episodeInt = 241;
					}
					else if (guid == "00000000-0000-0000-0000-0000f2000000")
					{
						// 2019-09-04 210000 - Troy Baker and Nolan North Are In Everything - Dude Soup Podcast (00000000-0000-0000-0000-0000f2000000).mp3
						episodeInt = 242;
					}
					else if (guid == "5476bdbc-a600-11ea-91ac-5f0dd9561553")
					{
						// 2020-06-04 020000 - Black Lives Matter - Dude Soup Podcast (5476bdbc-a600-11ea-91ac-5f0dd9561553).mp3
						episodeInt = 181;
					}

					// Later we swap from season episode to global episode number.
					// 2022-12-07 1400 - S08 E47 - Live from Our Brand New Completely Empty Studio! - Funhaus Podcast (f5fc7ef4-75ee-11ed-8557-2b8ca9348681).mp3
					// 2022-12-14 1400 - S08 E408 - All We Want for Christmas is Death Stranding 2 - Funhaus Podcast (993a5764-7b7e-11ed-ae45-3fcfb184407a).mp3
					
				}
				else if (podcast.Name == "Good Morning From Hell (FIRST Member Ad-Free)")
				{
					if (guid == "5321d644-6807-11eb-8f50-7798a363789e")
					{
						// 2021-02-08 0800 - S03 E6 - Wingmanning for Cupid #63 (5321d644-6807-11eb-8f50-7798a363789e).mp3
						episodeInt = 63;
					}
					else if (guid == "c683fbf2-af89-11eb-9d73-fb280123e5fa")
					{
						// 2021-05-10 0700 - E74 - D&D Ended Gus' Friendship (c683fbf2-af89-11eb-9d73-fb280123e5fa).mp3
						episodeInt = -1;
					}
				}
				else if (podcast.Name == "Hypothetical Nonsense (FIRST Member Ad-Free)" || podcast.Name == "Hypothetical Nonsense")
				{
					if (guid == "602f6064-597b-11ee-b43d-c75f60d46582" ||
					    guid == "62513d18-597b-11ee-b9ec-8b5b0ff24623")
					{
						// 2023-09-25 2000 - TAKING A HIT OF THE SHAQ PIPE (602f6064-597b-11ee-b43d-c75f60d46582).mp3
						episodeInt = 4;
					}
					else if (guid == "37a28184-6494-11ee-ab26-87b731b98b89" ||
					         guid == "359a34f4-6494-11ee-b832-cfbb4e6bdd0f")
					{
						//2023-10-09 200000 - E4 - Relationship Red Flags (37a28184-6494-11ee-ab26-87b731b98b89).mp3
						episodeInt = 6;
					}
					else if (guid == "a0a57490-88a5-11ee-b124-eb775b6270b5" ||
					         guid == "cdda7e42-88a5-11ee-9e1a-7f9f7ac5f4e8")
					{
						// 2023-11-27 210000 - E11 - The ULTIMATE Road Trip (a0a57490-88a5-11ee-b124-eb775b6270b5).mp3
						episodeInt = 13;
					}
				}
				else if (podcast.Name == "Must Be Dice (FIRST Member Ad-Free)" || podcast.Name == "Must Be Dice")
				{
					if (guid == "855db4d4-c2bf-11ec-a890-778d01c8fd9d" ||
					    guid == "5a37c632-c2bf-11ec-902e-d3bf5e3862fa")
					{
						// 2022-04-24 0700 - Stranger Than Stranger Things - Paradise Path RPG Ep 1 (855db4d4-c2bf-11ec-a890-778d01c8fd9d).mp3
						seasonInt = 1;
						episodeInt = 1;
					}
					else if (guid == "15cb58ea-c843-11ec-843f-e77608c59636" ||
					         guid == "04e78f9e-c843-11ec-a8f5-af5ec1a3defc")
					{
						// 2022-05-01 0700 - Trouble in Paradise - Paradise Path RPG Ep 2 (15cb58ea-c843-11ec-843f-e77608c59636).mp3
						seasonInt = 1;
						episodeInt = 2;
					}
					else if (guid == "d65fa1d8-cda2-11ec-b43f-ef5b8f967939" ||
					         guid == "b717eac4-cda2-11ec-b23f-6398ef3bc489")
					{
						// 2022-05-08 0700 - Mystery of the Humongous Fungus - Paradise Path RPG Ep 3 (d65fa1d8-cda2-11ec-b43f-ef5b8f967939).mp3
						seasonInt = 1;
						episodeInt = 3;
					}
					else if (guid == "c4e64ce8-ddfd-11ec-b54b-f359309aefaa")
					{
						// 2022-05-29 0700 - Sins of the Fathers, Children Unleashed - Paradise Path RPG Ep 6 (c4e64ce8-ddfd-11ec-b54b-f359309aefaa).mp3
						seasonInt = 1;
						episodeInt = 6;
					}
					else if (guid == "39bd3dbc-e37e-11ec-9098-8324b98aa2f6")
					{
						// 2022-06-05 0700 - Escape the Darkness, Behold the Truth - Paradise Path RPG Ep 7 (39bd3dbc-e37e-11ec-9098-8324b98aa2f6).mp3
						seasonInt = 1;
						episodeInt = 7;
					}
					else if (guid == "7974e882-4cb1-11ed-bba0-9fd4b0fa31d0" ||
					         guid == "5194fed8-4cb1-11ed-b5f1-8b661ff763c6")
					{
						// 2022-10-17 1300 - S02 E1 - Play, You Fools! - Super Princess Rescue Quest RPG Ep 1 (7974e882-4cb1-11ed-bba0-9fd4b0fa31d0).mp3
						episodeInt = 11;
					}
					else if (guid == "54143a44-599e-11ed-9e1b-dba62af9d44b" ||
					         guid == "34bd255c-599e-11ed-93d5-c37fd2ed6985")
					{
						// 2022-11-01 0700 - S02 E2 - A Hero Will Fall - Super Princess Rescue Quest RPG Ep 2 (54143a44-599e-11ed-9e1b-dba62af9d44b).mp3
						episodeInt = 12;
					}
					else if (guid == "9b71d468-5f2b-11ed-96c4-7fb5835ff94a" ||
					         guid == "7c930512-5f2b-11ed-859d-43f748da16c3")
					{
						// 2022-11-08 1400 - S02 E3 - Last Rites of the Great Frog King - Super Princess Rescue Quest RPG Ep 3 (9b71d468-5f2b-11ed-96c4-7fb5835ff94a).mp3
						episodeInt = 13;
					}
					else if (guid == "0da09b8e-6222-11ed-8edd-676a38c78e3a" ||
					         guid == "ec442e42-6221-11ed-b4c6-db441a3c1eab")
					{
						// 2022-11-14 1400 - S02 E4 - We Hold the Frog King's Oath Fulfilled - Super Princess Rescue Quest RPG Ep 4 (0da09b8e-6222-11ed-8edd-676a38c78e3a).mp3
						episodeInt = 14;
					}
					else if (guid == "87b6f39a-6831-11ed-b461-f7ecba4f8cfc" ||
					         guid == "623c8616-6831-11ed-8a8e-63be7e3cb80f")
					{
						// 2022-11-21 1400 - S02 E5 - To Die Fighting Side By Side with a Rat - Super Princess Rescue Quest RPG Ep 5 (87b6f39a-6831-11ed-b461-f7ecba4f8cfc).mp3
						episodeInt = 15;
					}
					else if (guid == "7ebdb894-6bb2-11ed-8f04-db104c00088f" ||
					         guid == "543c0bac-6bb2-11ed-bdaf-6f5d38f1dc10")
					{
						// 2022-11-28 1400 - S02 E6 - Taking the Battle to the Skies - Super Princess Rescue Quest RPG Ep 6 (7ebdb894-6bb2-11ed-8f04-db104c00088f).mp3
						episodeInt = 16;
					}
					else if (guid == "b6ec7bac-72cd-11ed-8ffb-e383de1638ba" ||
					         guid == "88ce688e-72cd-11ed-a96b-b3f3ad2f1656")
					{
						// 2022-12-05 1400 - S02 E7 - Neville's Meat is Back on the Menu - Super Princess Rescue Quest RPG Ep 7 (b6ec7bac-72cd-11ed-8ffb-e383de1638ba).mp3
						episodeInt = 17;
					}
					else if (guid == "d71ffb4a-7a54-11ed-8082-b7c62a3d5e51" ||
					         guid == "b1062588-7a54-11ed-9621-d73147cb30f0")
					{
						// 2022-12-12 2000 - S02 E19 - Flogging the Cyclops - Super Princess Rescue Quest RPG Ep 8 (d71ffb4a-7a54-11ed-8082-b7c62a3d5e51).mp3
						episodeInt = 18;
					}
				}
				else if (podcast.Name == "Off Topic (FIRST Member Ad-Free)" || podcast.Name == "Off Topic")
				{
					if (guid == "00000000-0000-0000-0000-00009b000000")
					{
						// 2018-11-17 180000 - The Soggy Golden Ticket (00000000-0000-0000-0000-00009b000000).mp3
						episodeInt = 155;
					}
					else if (guid == "00000000-0000-0000-0000-00009c000000")
					{
						// 2018-11-24 180000 - Gavin Almost Pukes in the First 5 Minutes (00000000-0000-0000-0000-00009c000000).mp3
						episodeInt = 156;
					}
					else if (guid == "e667132a-44d7-11ed-9eef-437af98e49ab")
					{
						// 2022-10-08 170100 - Queso Etiquette (e667132a-44d7-11ed-9eef-437af98e49ab).mp3
						title = $"POST SHOW - {title}";
					}
					else if (guid == "441130bc-7db5-11ed-872f-43a7c95d46c2")
					{
						// To fix the ordering
						// 2022-12-17 180100 - POST SHOW - Michael plans a Heist (441130bc-7db5-11ed-872f-43a7c95d46c2).mp3
						// 2022-12-18 184300 - E363 - Who Gave Nolan a NUKE! (2569133c-7db5-11ed-9e1d-57fa8334f2e6).mp3
						pubDate = pubDate.AddDays(1);
					}
					else
					{
						var offTopicEpisodeRegex = new Regex(@"^(.*)-([ ]*)#(?<episodeNumber>\d+)([ ]*)$");
						var match = offTopicEpisodeRegex.Match(title);
						if (match.Success)
						{
							if (Int32.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
							{
								episodeInt = tempEpisodeInt;
							}
						}
						else
						{
							var offTopicEpisodeRegex2 = new Regex(@"^(.*)([ ]*)#(?<episodeNumber>\d+)([ ]*)$");
							match = offTopicEpisodeRegex2.Match(title);
							if (match.Success)
							{
								if (Int32.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
								{
									episodeInt = tempEpisodeInt;
								}
							}
						}
					}
					
					// no post show
					// 2022-07-09 170000 - E342 - Ify Ate WHAT in the back of a Tesla (fc3c6156-fe45-11ec-a086-07ce60b9807b).mp3
					// 2023-07-15 170000 - E393 - Deep In The Meat Zone at RTX ft. Ify Nwadiwe and Fiona Nova (3797abfc-202d-11ee-94bd-1b5696487830).mp3
					
					// Later epsiodes have all POST SHOW as not having an episode number,
					// so we will do the same here
					if (title.StartsWith("POST SHOW", StringComparison.OrdinalIgnoreCase))
					{
						episodeInt = -1;
					}
					
				}
				else if (podcast.Name == "OT3 Podcast (FIRST Member Ad-Free)" || podcast.Name == "OT3 Podcast")
				{
					// The first few episodes never made it to audio podcast, these are all episode adjusted to match RT site.
					if (guid == "c4022560-e5a9-11eb-8416-afa3ee30c428")
					{
						episodeInt = 12;
					}
					else if (guid == "ebc5e47a-eb2a-11eb-9c0a-ef3d096d1a3d")
					{
						episodeInt = 13;
					}
					else if (guid == "9e422a2c-f0a9-11eb-86bd-1fb8ae90c81c")
					{
						episodeInt = 14;
					}
					else if (guid == "cfe27680-f640-11eb-95ea-d7e25d844805")
					{
						episodeInt = 15;
					}
					else if (guid == "7d5d7588-fbb6-11eb-a136-73bd1133f60e")
					{
						episodeInt = 16;
					}
					else if (guid == "39fe349a-0128-11ec-85da-439ce6a006f3")
					{
						episodeInt = 17;
					}
					else if (guid == "3cb820ce-06a5-11ec-bf52-f385be38511f")
					{
						episodeInt = 18;
					}
					else if (guid == "2e6ae14c-0c35-11ec-87f2-bf6f978fb24d")
					{
						episodeInt = 19;
						seasonInt = 2;
					}
					else if (guid == "f45fd0ce-11b2-11ec-9d8a-8fde4616556a")
					{
						episodeInt = 20;
					}
					else if (guid == "a329d9b4-1745-11ec-9ecf-73347b8c6a07" ||
					         guid == "6ba365e0-1746-11ec-9e6c-5f6eedabf0e0")
					{
						episodeInt = 21;
					}
					else if (guid == "206ee97c-1cc4-11ec-b7e9-6f0d3137946c" ||
					         guid == "dd12a914-1cc0-11ec-b997-af0380cebd6e")
					{
						episodeInt = 22;
						seasonInt = 2;
					}
					else if (guid == "cdd881b6-2234-11ec-bc6b-731228d3407b" ||
					         guid == "e2107dd2-2234-11ec-ba1f-c308f0c1d9d4")
					{
						episodeInt = 23;
					}
					else if (guid == "abbd3d28-27cb-11ec-98fb-2b4d20404e1a" ||
					         guid == "c683c618-27cb-11ec-bc06-4fdb5ff4ab5d")
					{
						episodeInt = 24;
					}
					else if (guid == "92d8ce5a-2d41-11ec-8e9b-376cc4a28457" ||
					         guid == "79414fe4-2d41-11ec-b7b0-13f146c5cbfe")
					{
						episodeInt = 25;
					}
					else if (guid == "3156417e-32a2-11ec-9cee-e7bd220254e5" ||
					         guid == "3a41bb38-32a2-11ec-b187-9bf21896df11")
					{
						episodeInt = 26;
					}
					else if (guid == "1a13ac16-383a-11ec-a009-7b463a2978be" ||
					         guid == "28792808-383a-11ec-a0f7-2bfe1bd2c112")
					{
						episodeInt = 27;
						seasonInt = 2;
					}
					else if (guid == "0a233108-3dc3-11ec-a6b0-9b389d0a400f" ||
					         guid == "303a94c6-3dc3-11ec-973e-5ffbc5c1b67a")
					{
						episodeInt = 28;
					}
					else if (guid == "214134a2-4354-11ec-9a29-c7c4f487c81d" ||
					         guid == "7295890c-4354-11ec-881e-67dd0fc155fa")
					{
						episodeInt = 29;
					}
					else if (guid == "613bf43a-48c6-11ec-be2f-e339af05c4db" ||
					         guid == "6df3443a-48c6-11ec-bdd0-d7f99125894f")
					{
						episodeInt = 30;
					}
					else if (guid == "c975f06c-4d87-11ec-b81a-5782d5f0a7e7" ||
					         guid == "cb1dbcf6-4d87-11ec-bd21-e3f41fc25d7a")
					{
						episodeInt = 31;
					}
					else if (guid == "ff3db3ec-53bd-11ec-ae1d-fbfa32cb97e3" ||
					         guid == "fcdaab82-53bd-11ec-8f1b-eb60d1c8e55d")
					{
						episodeInt = 32;
					}
					else if (guid == "c10c4eee-594e-11ec-94dc-bf61f8b3c424" ||
					         guid == "c26d3cee-594e-11ec-b361-d32353bf38c4")
					{
						episodeInt = 33;
					}
					else if (guid == "bd3b5d8a-5ec3-11ec-9c4c-dbf098ff5684" ||
					         guid == "becc28c8-5ec3-11ec-af9a-77bc38d63513")
					{
						episodeInt = 34;
					}

					if (seasonInt == 3)
					{
						episodeInt -= 22;
					}
						
					// 2021-10-16 070000 - S02 E13 - The Kingdom Hearts Fandom ().mp3
					// 2021-10-23 070000 - S02 E14 - Monsters You Want to Bang ().mp3
					// 2021-10-30 070000 - Bones or No Bones - TikTok Explained ().mp3
					// 2021-11-06 070000 - S02 E16 - Internet Community Changes the Media Landscape - NaNoWriMo ().mp3
					// 2021-11-13 080000 - S02 E17 - Real Vampires in New Orleans ().mp3
					// 2021-11-20 080000 - S02 E18 - Is Neopets Run By Scientologists ().mp3
					// 2021-11-27 080000 - S02 E19 - The Vampire Diaries with Michael Jones ().mp3
					// 2021-12-04 080000 - S02 E20 - Traumatizing a Fanfiction Author ().mp3
					// 2021-12-11 080000 - S02 E21 - A Fanfiction Writer to Published Novelist Talks To Us! ().mp3
					// 2021-12-18 080000 - S02 E22 - What's Your Dirty Pleasure From 2021 ().mp3
					// 2022-02-15 080000 - S03 E23 - Supernatural 101 - All 15 Seasons Explained (7f937a64-8b99-11ec-af74-23019c9f2744).mp3
					// 2022-02-22 080000 - S03 E24 - Did Wincest Win (wSpecial Guest BlackKrystel) (7d4d7546-9109-11ec-bc89-a38f66c47022).mp3
					// 2022-03-01 080000 - S03 E25 - Supernatural 301 - The Origin Story of Wincest (b65afda6-98b4-11ec-ae02-8f9d7fe8fd2e).mp3
					// 2022-03-08 080000 - S03 E26 - Spider-Man's Most Dangerous Foe - Broadway (3a2bd750-9c15-11ec-b790-ff6a7ccc503e).mp3
					// 2022-03-15 070000 - S03 E27 - Star Wars 101 - Skywalker Family DRAMA (wSpecial Guest Andy Blanchard) (cccbe652-a3b0-11ec-b05c-1b7f3d428c39).mp3
					// 2022-03-22 070000 - S03 E28 - Star War 201 - What the Hell Is the 'Force' (54b01350-a6d0-11ec-869b-6fc36c60f49e).mp3
					// 2022-03-29 070000 - S03 E29 - Star Wars 301 - Who's Kissing Who (wSpecial Guest Blaine Gibson) (10234b8a-ac89-11ec-a38b-87aa78ceb7f1).mp3
					// 2022-04-05 070000 - S03 E30 - Can Eldritch Horror Be Sexy - The Magnus Archives (c6cc6810-b230-11ec-bec0-ab542c4a6126).mp3
					// 2022-04-26 070000 - S03 E31 - Vampire Chronicles 101 - Who Would You Share a Coffin With (Anne Rice) (2681f1de-c290-11ec-8c9a-63722968fac2).mp3
					// 2022-05-03 070000 - S03 E32 - Anne Rice Vampires and Crucifixes- A Tale As Old As Time (4a3b2a88-c82a-11ec-8379-af9ec82cc097).mp3
					// 2022-05-10 070000 - S03 E33 - Our Flag Means Death - The Funky Gay Pirate Show (f1e9976c-cfad-11ec-bf37-d7b02ecfa747).mp3
					// 2022-05-17 070000 - S03 E34 - Heartstopper - The Gay Highschool Romance We All Wanted (d9e74e0e-d343-11ec-b0de-d7ec37f934ea).mp3
					// 2022-05-24 070000 - S03 E35 - Klaine - Glee Wasn't Great but at Least It Had Gay Representation (cd044692-d91f-11ec-93be-8746c4ea21df).mp3
					// 2022-06-01 070000 - S03 E36 - From Smut to Screen - Why is it SO POPULAR (ae8a4dae-e123-11ec-8786-ffcf5bb5dbe6).mp3
					// 2022-06-14 070000 - S03 E37 - Stranger Things And Kate Bush (61242030-eb6e-11ec-a72c-7f16a7fea5f5).mp3
					// 2022-06-22 194930 - S03 E38 - Charmed - Was It Too Much Nipple and Not Enough Plot wMatt Bragg (ee0b8882-f23d-11ec-991c-53ead9428054).mp3
					// 2022-06-28 070000 - S03 E39 - Doctor Who 101 but Wibbly Wobbly (616388e8-f63a-11ec-a7f6-33d69353c8f1).mp3
					// 2022-07-12 070000 - S03 E40 - Our Final Episode - Harry Potter & All the Young Dudes Fanfiction (81450054-ff15-11ec-8fec-b7138c46b901).mp3
					// 
					
					
					
					
					/*
					else if (guid == "a329d9b4-1745-11ec-9ecf-73347b8c6a07")
					{
						// 2021-09-17 0700 - S02 E10 - Lord of the Rings and Memes (a329d9b4-1745-11ec-9ecf-73347b8c6a07).mp3
						episodeInt = 9;
						seasonInt = 2;
					}
					else if (guid == "206ee97c-1cc4-11ec-b7e9-6f0d3137946c")
					{
						// 2021-09-24 0700 - E11 - Bad Comics Make Great TV (206ee97c-1cc4-11ec-b7e9-6f0d3137946c).mp3
						episodeInt = 10;
						seasonInt = 2;
					}
					else if (guid == "dd12a914-1cc0-11ec-b997-af0380cebd6e")
					{
						seasonInt = 2;
					}
					*/
				}
				else if (podcast.Name == "Red Web (FIRST Member Ad-Free)" || podcast.Name == "Red Web")
				{
					if (guid == "d96d1270-a472-11eb-ab80-9b12c8658daf")
					{
						// 2021-04-26 070000 - E36 - May Day Mystery (d96d1270-a472-11eb-ab80-9b12c8658daf).mp3
						seasonInt = 2;
					}
					else if (guid == "ea69546e-e8df-11ec-9d70-ff6258d6e3f7")
					{
						// 2022-06-12 070000 - E94 - Mysterious, Unsolved Puzzle at the CIA Headquarters, Kryptos Sculpture (ea69546e-e8df-11ec-9d70-ff6258d6e3f7).mp3
						seasonInt = 2;
					}
					else if (guid == "62d6d394-2b17-11ed-97aa-93e3030e72c6")
					{
						// 2022-09-04 070000 - E106 - The True Story of Annabelle the Doll, Annabelle the Doll (62d6d394-2b17-11ed-97aa-93e3030e72c6).mp3
						seasonInt = 2;
					}
					else if (guid == "352276c6-3092-11ed-8bb6-e3ec9dcb79e3")
					{
						// 2022-09-11 070000 - E107 - The Story Known as the 'Moonshine Murders', Brasher-Dye Disappearance (352276c6-3092-11ed-8bb6-e3ec9dcb79e3).mp3
						seasonInt = 2;
					}
					else if (guid == "b4063780-3730-11ed-9dfe-1b1e01effd16")
					{
						// 2022-09-18 070000 - E108 - The True Stories Behind Dracula, the Wolfman, and Gill-man, Three Movie Cryptids (b4063780-3730-11ed-9dfe-1b1e01effd16).mp3
						seasonInt = 2;
					}
					else if (guid == "b68feac2-9e7e-11ed-a896-ff75f464555d")
					{
						// 2023-01-29 080000 - E127 - How a Man Disappeared and Reappeared With No Explanation, Steven Kubacki Disappearance (b68feac2-9e7e-11ed-a896-ff75f464555d).mp3
						seasonInt = 2;
					}
					else if (guid == "a34d0b12-dd6f-11ee-a00c-475df08519d2")
					{
						// 2024-03-11 070000 - S02 E178 - What Is the Origin of This Creepy Urban Legend, Bunny Man (a34d0b12-dd6f-11ee-a00c-475df08519d2).mp3
						episodeInt = 179;
					}
					else if (guid == "3c1df836-5c38-11eb-90b9-8323a659fca3" || 
					         guid == "3c1df836-5c38-11eb-90b9-8323a659fca3")
					{
						// Is more of a supp episode. 
						// 2021-01-22 080000 - S02 E24 - Red Web Radio (3c1df836-5c38-11eb-90b9-8323a659fca3).mp3
						episodeInt = -1;
					}
				}
				else if (podcast.Name == "Rooster Teeth Podcast (FIRST Member Ad-Free)" || podcast.Name == "Rooster Teeth Podcast")
				{
					var rtpEpisodeRegex = new Regex(@"#(?<episodeNumber>\d+)([ ]*)$");
					var match = rtpEpisodeRegex.Match(title);
					if (match.Success)
					{
						if (Int32.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
						{
							episodeInt = tempEpisodeInt;
						}
					}

					if (title.Contains("POST SHOW"))
					{
						episodeInt = -1;
						pubDate = pubDate.AddMinutes(10);
					}

					if (guid == "8946ad28-e31d-11ee-a590-175b7df13273" ||
					    guid == "91bbdfe6-e31d-11ee-9474-ebf1ccbdcba7")
					{
						// 2024-03-18 190000 - E794 - The Rooster Teeth Podcast Live (8946ad28-e31d-11ee-a590-175b7df13273).mp3
						episodeInt = 793;
					}
					else if (guid == "7f23714c-e871-11ee-8888-836dbfd571bc" ||
					         guid == "77d83cec-e871-11ee-9e81-b765e828498e")
					{
						// 2024-03-25 190000 - E793 - Venmo vs Cashapp (7f23714c-e871-11ee-8888-836dbfd571bc).mp3
						episodeInt = 794;
					}
				}
				else if (podcast.Name == "Ship Hits The Fan (FIRST Member Ad-Free)" || podcast.Name == "Ship Hits The Fan")
				{
					// 2022-04-12 070000 - S01 E7 - Shifting Into Reverse Sinks the SS Norge - Ship Hits the Fan Podcast (722fd31e-ba14-11ec-a08f-77b2572bc51e).mp3
					// 2022-04-19 070000 - S01 E8 - A Shipwreck Graveyard Claims the Edmund Fitzgerald - Ship Hits the Fan Podcast (3ab8122c-bfa3-11ec-bc39-1beb97a70bf8).mp3
					// 2022-04-26 070000 - S01 - Mummies VS Billionaires - Titanic Conspiracy Theories - Ship Hits the Fan Podcast (33de7d10-c50c-11ec-ad6e-27c8c3bd1b6c).mp3
					// 2022-05-03 070000 - S01 E11 - Did Sea Monsters Really Sink a German U-Boat - Ship Hits the Fan Podcast (f81fca3c-ca91-11ec-b337-eb5cd44be56b).mp3
					// 2022-05-10 164500 - S01 E12 - The Legend of the Flying Canoe - Ship Hits the Fan Podcast (cd4c89a6-d07e-11ec-b4de-3b6b7e476ed2).mp3
					// 2022-05-17 070000 - S02 E9 - A Game of Maritime Chicken Blows the Hell Out of Halifax - Ship Hits the Fan Podcast (0f409b42-d596-11ec-9152-2bc0e457f7a2).mp3
					// 2022-05-24 070000 - S02 E10 - Magicians and Cover Bands Save the MTS Oceanos - Ship Hits the Fan Podcast (2ca60218-db17-11ec-b111-b3a5a6a5138e).mp3
					// 2022-05-31 070000 - S02 E11 - The Lioness of Brittany Stains the Seas Red - Ship Hits the Fan Podcast (4f0dc764-de50-11ec-a5af-bf2c85f37eb6).mp3

					// 2022-07-26 070000 - S02 E19 - An Impromptu Boat Race Leads to Decades of War - Ship Hits the Fan Podcast (3f135022-0c95-11ed-b7eb-d3cd40f4f1f6).mp3
					// 2022-08-02 070000 - S02 E20 - Soviets Drive Fleeing Nazis Into and Under the Sea - Ship Hits the Fan Podcast (01927e56-1219-11ed-b921-7336b432c736).mp3
					// 2022-08-09 070000 - S02 - Japanese Fishermen Catch an Alien or Maybe Some Russian Lady - Ship Hits the Fan Podcast (6881e286-1760-11ed-98c8-2357ac6ea3e6).mp3
					// 2022-08-16 070000 - S02 - Sin City of the Gulf Claims the SS Selma - Ship Hits the Fan Podcast (45d128c6-1cee-11ed-bb0a-8b625e4ba4fb).mp3
					// 2022-08-23 070000 - S02 - Spooky Abandoned Vessels and Ghost Ships - Ship Hits the Fan Podcast (147ccb5c-224f-11ed-94c2-7737192636ea).mp3
					// 2022-08-30 070000 - S03 E21 - The Tragic True Story That Inspired Moby Dick - Ship Hits the Fan Podcast (d2a4d474-27ec-11ed-ba38-53beb5c238a0).mp3
					// 2022-09-06 070000 - S03 E22 - A 17th Century Shipwreck Turns Men into Monsters - Ship Hits the Fan Podcast (f41083b0-2a71-11ed-9e2f-a7f31681c491).mp3

					// 2022-12-06 080000 - S03 E32 - The Real Story of the USS Indianapolis Part 2 - Ship Hits the Fan Podcast (47131702-74f6-11ed-861d-63ae5b3e05f0).mp3
					// 2022-12-13 080000 - S03 E33 - The Philadelphia Experiment with Elyse Willems - Ship Hits the Fan Podcast (03ab46fc-7a77-11ed-9829-3fd0dc744928).mp3
					// 2022-12-20 080000 - S03 - Apocryphal Maritime Weapons of the Ancient World - Ship Hits the Fan Podcast (b9b413ce-7ff0-11ed-8f54-7fa45867e738).mp3
					// 2022-12-27 080000 - S03 - A History of Cats at Sea - Ship Hits the Fan Podcast (88994616-8256-11ed-9136-1721ddef62f5).mp3
					// 2023-01-03 080000 - S04 E33 - Blackbeard Steals, Exploits, and Sinks the Queen Anne - Ship Hits the Fan Podcast (ca05ac94-8269-11ed-a8c2-7338304d161b).mp3
					// 2023-01-10 080000 - S04 E34 - Caligula's Party Barges - Ship Hits the Fan Podcast (10699946-9077-11ed-bd23-934eb1ad94dc).mp3

					// 2023-03-21 070000 - S04 E44 - The Great Lakes Were No Match for The Big Blow - Ship Hits the Fan Podcast (48310c54-c756-11ed-b67c-f7681e44502c).mp3
					// 2023-03-28 070000 - S04 - Ship Hits the Fan will be back with a brand new episode next week! (3574bf72-cb1e-11ed-a5d8-9bb193bf320d).mp3
					// 2023-04-04 070000 - S04 - What Sunk the Floating Crypto Bro Utopia - Ship Hits the Fan Podcast (b9d48e10-d270-11ed-bdca-73130a32b010).mp3
					// 2023-04-18 070000 - S05 E45 - Germany Sinks the RMS Lusitania - Ship Hits the Fan Podcast (d540b4d0-dd56-11ed-a1bb-53328680e463).mp3
				}
				else if (podcast.Name == "So... Alright (FIRST Member Ad-Free)" || podcast.Name == "So... Alright")
				{
					if (guid == "7316ad24-4e75-11ee-9051-bb3bc7db730a")
					{
						// 2023-09-04 0700 - S01 E2 - I Know Who Shot JR (7ba3cf0a-48f6-11ee-80e7-cbef7e41727b).mp3
						// 2023-09-11 0700 - S01 E4 - I saw dead people (7316ad24-4e75-11ee-9051-bb3bc7db730a).mp3
						// 2023-09-19 0700 - S01 E4 - This One Goes to 11 (7b8d7400-531c-11ee-8155-6fa1067b4a18).mp3
						episodeInt = 3;
					}
					else if (guid == "dcdb748e-5bf8-11ee-80a8-4fa6df58d975")
					{
						// 2023-09-26 070000 - E5 - Second Time's a Charm (dcdb748e-5bf8-11ee-80a8-4fa6df58d975).mp3
						seasonInt = 1;
					}
					else if (guid == "213367dc-8254-11ee-b7d8-7798dd0fd8d5" ||
					         guid == "23e1bd58-8254-11ee-94b6-5bb9af6ceeeb")
					{
						// E12 - Fantastic Man (213367dc-8254-11ee-b7d8-7798dd0fd8d5).mp3
						seasonInt = 1;
					}
					else if (guid == "78f7be26-c473-11ee-a2d7-17bf8060e49d" ||
					         guid == "9cd2b378-c473-11ee-a96b-9fb30dff80c8")
					{
						// E24 - Ephemeral Ants (78f7be26-c473-11ee-a2d7-17bf8060e49d).mp3
						seasonInt = 2;
					}
					else if (guid == "c9ff103a-eaed-11ee-be0f-9f74d46fd564" ||
					         guid == "a509117c-eaed-11ee-a3fa-dfa62eec9ae4")
					{
						// 2024-03-26 070000 - E31 - Mall Thoughts (c9ff103a-eaed-11ee-be0f-9f74d46fd564).mp3
						seasonInt = 2;
					}
				}
				else if (podcast.Name == "Tales from the Stinky Dragon (FIRST Member Ad-Free)")
				{
					if (guid == "4914cf7a-e5de-11ec-a073-8f6acd7e4a1c")
					{
						// Supplemental 
						// 2022-06-07 070000 - S01 E53 - Paralyte's Poison - Between The Tales (4914cf7a-e5de-11ec-a073-8f6acd7e4a1c).mp3
						episodeInt = -1;
					}
					else if (guid == "54f4c7e8-0157-11ed-ae63-4bba2a713e21")
					{
						// Supp
						// 2022-07-12 070000 - S01 E57 - Stinky Dragon Side Quest - Honey Heist 2022 (54f4c7e8-0157-11ed-ae63-4bba2a713e21).mp3
						episodeInt = -1;
					}
					else if (guid == "a801d7c8-f364-11ed-9555-eb1f4695634a")
					{
						// 2023-05-16 0700 - S02 E5 - Arrested in Attro City - C02 Ep 04 - Give It the Old College Spy (a801d7c8-f364-11ed-9555-eb1f4695634a).mp3
						episodeInt = 4;
					}
					else if (guid == "f7d2bc90-8d6f-11ee-b3ce-5749a2ebd8cb")
					{
						// 2023-11-28 0800 - [Second Wind] Gems of Glrrb - C02 - Ep 25 - Secrets and Summonings (f7d2bc90-8d6f-11ee-b3ce-5749a2ebd8cb).mp3
						// 2023-11-28 0800 - S02 E25 - Gems of Glrrb - C02 - Ep 25 - Secrets And Summonings (0a72100e-8d6f-11ee-a4af-a7383507ac2a).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "1401edf2-92dc-11ee-92d9-3b0dee24d9f8")
					{
						// 2023-12-05 0800 - [Second Wind] Gems of Glrrb - C02 - Ep 25.5 - Between The Tales (1401edf2-92dc-11ee-92d9-3b0dee24d9f8).mp3
						// 2023-12-05 0800 - S02 - Gems of Glrrb - C02 - Ep 25.5 - Between The Tales (544239c8-92da-11ee-b269-a7da1e1b83f9).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "f95ff3e6-9861-11ee-b4a4-a70425192ac4")
					{
						// 2023-12-12 0800 - [Second Wind] Passé in Perrish - C02 - Ep 26 - Cart Before The Horseman (f95ff3e6-9861-11ee-b4a4-a70425192ac4).mp3
						// 2023-12-12 0800 - S02 E26 - Passé in Perrish - C02 - Ep 26 - Cart Before The Horseman (94252114-9860-11ee-afa7-33eb7a99865a).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "f95ff3e6-9861-11ee-b4a4-a70425192ac4")
					{
						// 2023-12-12 0800 - [Second Wind] Passé in Perrish - C02 - Ep 26 - Cart Before The Horseman (f95ff3e6-9861-11ee-b4a4-a70425192ac4).mp3
						// 2023-12-12 0800 - S02 E26 - Passé in Perrish - C02 - Ep 26 - Cart Before The Horseman (94252114-9860-11ee-afa7-33eb7a99865a).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "e1a4608c-9e03-11ee-a237-931fcc53a2f5")
					{
						// 2023-12-19 0800 - [Second Wind] Passé in Perrish - C02 - Ep 27 - The Quick and the Undead (e1a4608c-9e03-11ee-a237-931fcc53a2f5).mp3
						// 2023-12-19 0800 - E27 - Passé in Perrish - C02 - Ep 27 - The Quick and the Undead (33278e12-9e03-11ee-b8d5-b7146b76bebf).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "35fdf744-9eb6-11ee-8c39-5364b2317695")
					{
						// 2023-12-26 0800 - E28 - [Second Wind] Passé in Perrish - C02 - Ep 28 - Rest in Priest (35fdf744-9eb6-11ee-8c39-5364b2317695).mp3
						// 2023-12-26 0800 - E28 - Passé in Perrish - C02 - Ep 28 - Rest in Priest (30c0c68c-9eb4-11ee-ae82-8ffb226ac95c).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "d52fd1e2-ae57-11ee-956e-972e02120abb")
					{
						// 2024-01-09 0800 - [Second Wind] Passé in Perrish - C02 - Ep 30 - Hollow Be Thy Name (d52fd1e2-ae57-11ee-956e-972e02120abb).mp3
						// 2024-01-09 0800 - S02 E30 - Passé in Perrish - C02 - Ep 30 - Hollow Be Thy Name (c11f35f8-ae57-11ee-ae47-b3eb958e1446).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "e2644100-b437-11ee-bf99-9b952645355f")
					{
						//  2024-01-16 0800 - S02 E32 - Passé in Perrish - C02 - Ep 31 - Astralian Arrival (e2644100-b437-11ee-bf99-9b952645355f).mp3
						episodeInt = 31;
					}
					else if (guid == "424f41a4-b961-11ee-aeb3-83ca69857ddc")
					{
						// 2024-01-23 0800 - [Second Wind] Passé in Perrish - C02 - Ep 32 - In Case You Alchemist It (424f41a4-b961-11ee-aeb3-83ca69857ddc).mp3
						// 2024-01-23 0800 - S02 E32 - Passé in Perrish - C02 - Ep 32 - In Case You Alchemist It (20db571e-b95d-11ee-bfc6-7fd631b9f2a9).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "1bdac810-bf04-11ee-8818-f36e8674134f")
					{
						// 2024-01-30 0800 - [Second Wind] Passé in Perrish - C02 - Ep 33 - All Your Hags in One Basket (1bdac810-bf04-11ee-8818-f36e8674134f).mp3
						// 2024-01-30 0800 - S02 E33 - Passé in Perrish - C02 - Ep 33 - All Your Hags in One Basket (fe24be1c-bf02-11ee-a7e5-c73ee85eac94).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "ef52a6b6-c479-11ee-ad03-23b9ca0e4049")
					{
						// 2024-02-06 0800 - S02 - [Second Wind] Between the Tales- C02 - Ep 33.5 (ef52a6b6-c479-11ee-ad03-23b9ca0e4049).mp3
						// 2024-02-06 0800 - S02 - Passé in Perrish - Between the Tales- C02 - Ep 33.5 (25f035d4-c476-11ee-92c5-e3bd3a13ec58).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "33278e12-9e03-11ee-b8d5-b7146b76bebf")
					{
						// 23-12-19 080000 - E27 - Passé in Perrish - C02 - Ep 27 - The Quick and the Undead (33278e12-9e03-11ee-b8d5-b7146b76bebf).mp3
						seasonInt = 2;
					}
					else if (guid == "30c0c68c-9eb4-11ee-ae82-8ffb226ac95c")
					{
						// 2023-12-26 080000 - E28 - Passé in Perrish - C02 - Ep 28 - Rest in Priest (30c0c68c-9eb4-11ee-ae82-8ffb226ac95c).mp3
						seasonInt = 2;
					}
					else if (guid == "35fdf744-9eb6-11ee-8c39-5364b2317695")
					{
						// 2023-12-26 081000 - E28 - [Second Wind] Passé in Perrish - C02 - Ep 28 - Rest in Priest (35fdf744-9eb6-11ee-8c39-5364b2317695).mp3
						episodeInt = -1;
					}
					else if (guid == "ba9b4768-a983-11ee-929b-579afd30b2e2")
					{
						// 2024-01-02 152900 - S02 E29 - [Second Wind] Passé in Perrish - C02 - Ep 29 - Whole Hag of Tricks (ba9b4768-a983-11ee-929b-579afd30b2e2).mp3
						episodeInt = -1;
					}
					else if (guid == "cf0bcb26-b439-11ee-b050-673c5fb10f6d")
					{
						// 2024-01-16 081000 - S02 E31 - [Second Wind] Passé in Perrish - C02 - Ep 31 - Astralian Arrival (cf0bcb26-b439-11ee-b050-673c5fb10f6d).mp3
						episodeInt = -1;
					}
					else if (guid == "ef52a6b6-c479-11ee-ad03-23b9ca0e4049")
					{
						// 2024-02-06 081000 - S02 - [Second Wind] Between the Tales- C02 - Ep 33.5 (ef52a6b6-c479-11ee-ad03-23b9ca0e4049).mp3
						seasonInt = -1;
					}
					else if (guid == "95fa4dbe-cd05-11ee-a8b6-db46ceedfb73")
					{
						// 2024-02-20 080000 - [Second Wind] Vengeance in Vania - C02 - Ep 35 - Velcome to Vainia (95fa4dbe-cd05-11ee-a8b6-db46ceedfb73).mp3
						pubDate = pubDate.AddMinutes(10);
					}
					else if (guid == "4914cf7a-e5de-11ec-a073-8f6acd7e4a1c")
					{
						// Don't give it an episode number to match the other between the tales.
						// 2022-06-07 070000 - S01 E53 - Paralyte's Poison - Between The Tales (4914cf7a-e5de-11ec-a073-8f6acd7e4a1c).mp3
						episodeInt = -1;
					}
					else if (guid == "72688b46-eaf0-11ee-afc2-df0c550e8b98")
					{
						//2024-03-26 070000 - [Second Wind] Klawstred Way To The Top - C02 - Ep 40 (72688b46-eaf0-11ee-afc2-df0c550e8b98).mp3
						pubDate = pubDate.AddMinutes(10);
					}

					if (title.Contains("Between the Tales-"))
					{
						title = title.Replace("Between the Tales-", "Between the Tales -");
					}

					if (title.Contains("[Second Wind]"))
					{
						episodeInt = -1;
						seasonInt = -1;
					}
					
				}
				else if (podcast.Name == "The Dogbark Podcast (FIRST Member Ad-Free)")
				{
					if (guid == "9dfb77c2-6243-11ee-83bc-7b115221cf49")
					{
						// 2023-10-04 150000 - E1 - The Dogbark Podcast - Ep. 1 (9dfb77c2-6243-11ee-83bc-7b115221cf49).mp3
						seasonInt = 1;
					}
					else if (guid == "eb14da22-67bf-11ee-93c0-17290f6631e7")
					{
						// 2023-10-11 150000 - E2 - The Dogbark Podcast - Ep. 2 (eb14da22-67bf-11ee-93c0-17290f6631e7).mp3
						seasonInt = 1;
					}
					
					// Gets rid of things like S2:E18 out of the front as we already have them.
					var dogBarkTitleRegex = new Regex(@"^S(\d+):E(:*)(\d+)([ \-]*)(?<title>.*)");
					var match = dogBarkTitleRegex.Match(title);
					if (match.Success)
					{
						title = match.Groups["title"].Value;
					}
				}
				else if (podcast.Name == "The Most (FIRST Member Ad-Free)")
				{
					// two episode 49s, is one a sup?
					// 2022-01-30 080000 - S02 E48 - Fabulous Abacus (36606c00-8070-11ec-897a-dfa38e54a802).mp3
					// 2022-02-06 080000 - S02 E49 - Don't Feed The Bears (11036ae6-85e0-11ec-bd03-57b9439edf8d).mp3
					// 2022-02-13 080000 - S02 E49 - We Like The Slop (19f10ce6-8b92-11ec-bee3-bfa285442452).mp3
					// 2022-02-20 080000 - S02 E50 - Now That's What I Call Monsters (5eed4a04-90ff-11ec-9611-f31056cb5949).mp3
					
					// Those above also don't align with online episodes.
					// 2022:E4 - Fabulous Abacus
					// 2022:E5 - Don't Feed The Bears
					// 2022:E6 - We Like The Slop
					// 2022:E7 - Now That's What I Call Monsters

					
					
					// These don't align to release dates with RT site, so ignoring.
					// 2020:E4 - Quicksand 9/11
					// 2020:E9 - Smörgås Borg
					// 2021:E12 - Earthworm Gym
				}
				else if (podcast.Name == "Trash for Trash (FIRST Member Ad-Free)" || podcast.Name == "Trash for Trash")
				{
					// Perfect, no notes.
				}
				else if (podcast.Name == "Black Box Down")
				{
					// Perfect, no notes.
				}
				else if (podcast.Name == "Filmhaus Podcast")
				{
					// There are no numbers, not really anything to do.
					// We can use GuidStrToInt but then halfway through we change to actual so that won't work.
				}
				else if (podcast.Name == "Glitch Please")
				{
					if (guid == "00000000-0000-0000-0000-00001b000000")
					{
						// 2017-12-01 180000 - Return Of The Low Ping Bastards (00000000-0000-0000-0000-00001b000000).mp3
						episodeInt = 27;
					}
					var glitchPleaseRegex = new Regex(@"^(.*)-(\s*)#(?<episodeNumber>\d+)$");
					var match = glitchPleaseRegex.Match(title);
					if (match.Success)
					{
						if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
						{
							episodeInt = tempEpisodeInt;
						}
					}

					if (episodeInt == -1)
					{
						episodeInt = GuidStrToInt(guid);
					}
				}
				else if (podcast.Name == "Good Morning From Hell")
				{
					// No notes, perfect
				}
				else if (podcast.Name == "Heroes & Halfwits")
				{
					if (guid == "6c4d7f32-ad36-11eb-ab40-cf0bc9502bea")
					{
						// 2021-05-05 070000 - Introducing Tales from the Stinky Dragon (6c4d7f32-ad36-11eb-ab40-cf0bc9502bea).mp3
						// Noop
					}
					else
					{
						episodeInt = GuidStrToInt(guid);
						seasonInt = 1;
						
						if (episodeInt >= 35 && episodeInt <= 40)
						{
							seasonInt = 2;
							episodeInt -= 34;
						}
						else if (episodeInt >= 41 && episodeInt <= 50)
						{
							seasonInt = 3;
							episodeInt -= 40;
						}
					}
				}
				else if (podcast.Name == "Inside Gaming Roundup")
				{
					// There isn't really any episode numbers.
				}
				else if (podcast.Name == "No Dumb Answers with Mark & Brad")
				{
					if (guid == "f5fade30-86a0-11eb-8f89-1760929da766")
					{
						// 2021-03-17 070000 - E3 - Hot Girls Have IBS Feat. Elyse Willems (f5fade30-86a0-11eb-8f89-1760929da766).mp3
						seasonInt = 1;
					}
					else if (guid == "c4243ca8-8c09-11eb-bfd1-6f4dc5d83125")
					{
						// 2021-03-24 070000 - E4 - You Can't F Past the Balls (c4243ca8-8c09-11eb-bfd1-6f4dc5d83125).mp3
						seasonInt = 1;
					}
					else if (guid == "52398a40-9194-11eb-a7f6-e31e38b529ac")
					{
						// 2021-03-31 070000 - E5 - Brony Liquid & TikTok Cults (52398a40-9194-11eb-a7f6-e31e38b529ac).mp3
						seasonInt = 1;
					}
					else if (guid == "80b1b918-9722-11eb-8ba1-cf91521cf9d7")
					{
						// 2021-04-07 070000 - E6 - Bad Btches Doing Jiu-Jitsu (80b1b918-9722-11eb-8ba1-cf91521cf9d7).mp3
						seasonInt = 1;
					}
					else if (guid == "491f1c8c-9c60-11eb-9f8e-276b43c8fa7e")
					{
						// 2021-04-14 070000 - E7 - Joseph Stalin - Girl Boss (491f1c8c-9c60-11eb-9f8e-276b43c8fa7e).mp3
						seasonInt = 1;
					}
					else if (guid == "7d6141c2-a20b-11eb-98ea-df2e258863cb")
					{
						// 2021-04-21 070000 - E8 - Cancel Culture Must Be Stopped (7d6141c2-a20b-11eb-98ea-df2e258863cb).mp3
						seasonInt = 1;
					}
					else if (guid == "4cdea518-c320-11eb-9965-4354bebbdf0c")
					{
						// 2021-06-02 070000 - E9 - Legally Obligated Returns (4cdea518-c320-11eb-9965-4354bebbdf0c).mp3
						episodeInt = 1;
						seasonInt = 2;
					}
				}
				else if (podcast.Name == "On The Spot")
				{
					// No episode 10, 68, 133, 171
					var onTheSpotRegex = new Regex(@"^(.*)(-|)(\s*)#(?<episodeNumber>\d+)(\s*)$");
					var match = onTheSpotRegex.Match(title);
					if (match.Success)
					{
						if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
						{
							episodeInt = tempEpisodeInt;
						}
					}

					if (episodeInt == -1)
					{
						onTheSpotRegex = new Regex(@"^(.*)(-|)(\s*)#(?<episodeNumber>\d+) (\(|f|F)");
						match = onTheSpotRegex.Match(title);
						if (match.Success)
						{
							if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
							{
								episodeInt = tempEpisodeInt;
							}
						}
					}

					if (episodeInt == -1)
					{
						onTheSpotRegex = new Regex(@"^(.*)-(\s*)(?<episodeNumber>\d*)(\s*)$");
						match = onTheSpotRegex.Match(title);
						if (match.Success)
						{
							if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
							{
								episodeInt = tempEpisodeInt;
							}
						}
					}

					if (episodeInt == -1)
					{
						if (guid == "00000000-0000-0000-0000-00004c000000")
						{
							// 2016-11-24 182500 - Forever Alone - On The Spot - A Thanksgiving Special (00000000-0000-0000-0000-00004c000000).mp3
							episodeInt = 76;
						}
						else if (guid == "00000000-0000-0000-0000-000038000000")
						{
							// 2016-04-01 160000 - FCK BOIZ NEVER SAY DIE! - #56 with Brent Morin (00000000-0000-0000-0000-000038000000).mp3
							episodeInt = 56;
						}
						else if (guid == "388da800-5ede-11ee-b966-bbc91e8ba6e0")
						{
							// 2023-09-29 170000 - Barbies Vs. Hobbits (388da800-5ede-11ee-b966-bbc91e8ba6e0).mp3
							episodeInt = 186;
						}
					}
				}
				else if (podcast.Name == "Relationship Goals")
				{
					episodeInt = GuidStrToInt(guid);	
				}
				else if (podcast.Name == "RWBY Rewind")
				{
					var rwbyRewindRegex = new Regex(@"^(.*)-(\s*)#(?<episodeNumber>\d+)$");
					var match = rwbyRewindRegex.Match(title);
					if (match.Success)
					{
						if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
						{
							episodeInt = tempEpisodeInt;
						}
					}
				}
				else if (podcast.Name == "Tales from the Stinky Dragon")
				{
					if (guid == "36fa2fc2-9e03-11ee-b3a9-4f8947c55357")
					{
						// 023-12-19 080000 - E27 - Passé in Perrish - C02 - Ep 27 - The Quick and the Undead (36fa2fc2-9e03-11ee-b3a9-4f8947c55357).mp3
						seasonInt = 2;
					}
					else if (guid == "388bee3c-9eb4-11ee-ace2-cbbc38baa563")
					{
						// 2023-12-26 080000 - E28 - Passé in Perrish - C02 - Ep 28 - Rest in Priest (388bee3c-9eb4-11ee-ace2-cbbc38baa563).mp3
						seasonInt = 2;
					}
				}
				else if (podcast.Name == "Twits and Crits")
				{
					if (guid == "00000000-0000-0000-0000-00001f000000")
					{
						episodeInt = 31;
					}
					
					var twitsAndCritsRegex = new Regex(@"^(.*)- Episode (?<episodeNumber>\d+)$");
					var match = twitsAndCritsRegex.Match(title);
					if (match.Success)
					{
						if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
						{
							episodeInt = tempEpisodeInt;
						}
					}
				}
				else if (podcast.Name == "unLOCKED - The Official genLOCK Companion Podcast")
				{
					// Perfect, no notes.
				}
				else if (podcast.Name == "The Real Canon")
				{
					if (guid == "0116d0b4-721d-11eb-932c-4b3a6cc0cddf")
					{
						// RSS feed says this is episode 5 and the WandaVision is a bonus episode.
						// 2021-02-19 080000 - Jim Henson didn't make The Muppets for you (0116d0b4-721d-11eb-932c-4b3a6cc0cddf).mp3
						episodeInt = 5;
					}
				}
				else if (podcast.Name == "The Most")
				{
					if (guid == "aaa70566-38f8-11ec-9ad6-9717101cf2ba")
					{
						// 2021-11-01 070000 - E47 - Abduction Junction (aaa70566-38f8-11ec-9ad6-9717101cf2ba).mp3
						episodeInt = 38;
						seasonInt = 2;
					}
					
					// These don't align to release dates with RT site, so ignoring.
					// 2020:E4 - Quicksand 9/11
					// 2020:E9 - Smörgås Borg
					// 2021:E12 - Earthworm Gym
				}
				else if (podcast.Name == "CHUMP")
				{
					if (episodeInt >= 26)
					{
						// Everything after 26 is season 4.
						seasonInt = 4;
					}
				}
				else if (podcast.Name == "Class Of 198X")
				{
					
					if (guid == "00000000-0000-0000-0000-000010000000")
					{
						// 2018-04-25 140000 - TEARS OF A TEENAGE WASTELAND (00000000-0000-0000-0000-000010000000).mp3
						episodeInt = 16;
					}
					else
					{
						var classOf198XRegex = new Regex(@"^(.*) - Part (?<episodeNumber>\d+)$");
						var match = classOf198XRegex.Match(title);
						if (match.Success)
						{
							if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
							{
								episodeInt = tempEpisodeInt;
								title = title.Replace($" - Part {episodeInt}", string.Empty);
							}
						}
					}
				}
				else if (podcast.Name == "Enjoy the Show")
				{
					episodeInt = GuidStrToInt(guid);
				}
				else if (podcast.Name == "Fan Service")
				{
					if (guid == "a320ab90-37b4-11ea-9975-b3280f8f18e9")
					{
						// 2020-01-15 160000 - Our First Bad Anime (a320ab90-37b4-11ea-9975-b3280f8f18e9).mp3
						episodeInt = 75;
					}
					else if (guid == "ee4266d2-3d35-11ea-88d2-5b3cd191368d")
					{
						// 2020-01-22 160000 - Most Surprising Anime of 2020 (ee4266d2-3d35-11ea-88d2-5b3cd191368d).mp3
						episodeInt = 76;
					}
					else
					{
						episodeInt = GuidStrToInt(guid);
						title = title.Replace($"- #0{episodeInt}", string.Empty).Trim();
					}
				}
				else if (podcast.Name == "I Have Notes")
				{
					// Perfect, ironically no notes.
				}
				else if (podcast.Name == "Inside Gaming Daily")
				{
					// No episode 99
					// Two episode 127
					// Two episode 133
					// Two episode 138
					
					// Duplicate episide numbers 161 - 169
					// 2020-09-02 000507 - S20 E169 - About PS5's Backwards Compatibility... (087c916a-ecb0-11ea-8c26-abf04ce43dd9).mp3
					// 2020-09-03 001527 - S20 E170 - GameSpot Sponsorship Pisses Off GameSpot (bc5d2a44-ed7a-11ea-a8d7-6b7b5477f2a8).mp3
					// 2020-09-03 234735 - S20 E161 - 3D Mario Rumors CONFIRMED For Switch! (089f9592-ee41-11ea-99ee-dbe14b890af7).mp3
					// 2020-09-07 230000 - S20 E162 - No Man's Sky Devs Making 'Huge' Game... hmmmm (042720cc-eef8-11ea-94f3-6f15abd6a58d).mp3
					// 2020-09-08 231027 - S20 E163 - Series S price puts Microsoft in the lead (d251a250-f228-11ea-9d8c-d3e4aa68e97d).mp3
					// 2020-09-09 224838 - S20 E164 - Xbox Series X launch date and price confirmed, Game Pass gets better (d530fcc0-f2ee-11ea-8e45-f7818ae460b2).mp3
					// 2020-09-10 234100 - S20 E165 - It's worse for GameStop than we thought (f4aab5b4-f3c0-11ea-bff3-8368be95cb8a).mp3
					// 2020-09-11 215031 - S20 E166 - Publisher lied about Control Ultimate Edition (d78f64ee-f482-11ea-86ee-ebc54003d0e5).mp3
					// 2020-09-15 004130 - S20 E167 - Xbox Series S has one very annoying limitation (e23f1eaa-f6eb-11ea-ba1f-3786b452cec7).mp3
					// 2020-09-16 010904 - S20 E168 - PS5 takes some heat just before price and date reveal (3fcc6976-f7b2-11ea-940f-67e7b2e8a62f).mp3
					// 2020-09-17 000531 - S20 E169 - The PS5 will cost $500! (7a19c26a-f878-11ea-85e2-af53013e2faa).mp3
					// 2020-09-17 235600 - S20 E170 - Good luck getting a PS5 (7c14a368-f941-11ea-ab3c-2b67855473c8).mp3
					
					// Two episode 203
					// Two episode 230

					if (guid == "b123dc7c-2dee-11eb-8d67-f3f66800ccc1")
					{
						// 2020-11-24 004143 - S2020 E215 - Cyberpunk 2077 has leaked (b123dc7c-2dee-11eb-8d67-f3f66800ccc1).mp3
						seasonInt = 20;
					}
					else if (guid == "e3dbd3c8-4eef-11eb-885a-fb5ded1ea49e")
					{
						// 2021-01-05 010000 - Farewell Inside Gaming Daily (e3dbd3c8-4eef-11eb-885a-fb5ded1ea49e).mp3
						seasonInt = 20;
						episodeInt = 244;
					}
				}
				else if (podcast.Name == "Inside Gaming Podcast")
				{
					if (guid == "3cf68d5c-e903-11ea-be1d-43c7d2280fdf")
					{
						// 2020-08-28 110000 - The Halo Infinite Panic Button - Send News #27 (3cf68d5c-e903-11ea-be1d-43c7d2280fdf).mp3
						episodeInt = 27;
						seasonInt = 20;
					}
					else if (guid == "3ed116ec-42ae-11eb-a048-7be4a06f42ad")
					{
						// 2020-12-25 120000 - The All Questions Episode (3ed116ec-42ae-11eb-a048-7be4a06f42ad).mp3
						episodeInt = 44;
						seasonInt = 20;
					}
				}
				else if (podcast.Name == "Murder Room")
				{
					episodeInt = GuidStrToInt(guid);
					
					// This starts at episode 2 because episode 1 was a pilot.
				}
				else if (podcast.Name == "Sportsball")
				{
					episodeInt = GuidStrToInt(guid);
					if (episodeInt >= 27)
					{
						seasonInt = 2;
						episodeInt -= 26;
					}
				}
				else if (podcast.Name == "The Bungalow - The Business of Rooster Teeth")
				{
					episodeInt = GuidStrToInt(guid);
				}
				else if (podcast.Name == "The Patch")
				{
					episodeInt = GuidStrToInt(guid);
				}
				else if (podcast.Name == "Theater Mode AUX")
				{
					// These episode numbers (via guids) and release dates are all over the place.
					// So we just leave as is.
				}
				else if (podcast.Name == "Twits and Crits - The League of Extraordinary Jiremen")
				{
					var cacTheLeagueRegex = new Regex(@"^(.*): Part (?<episodeNumber>\d+)$");
					var match = cacTheLeagueRegex.Match(title);
					if (match.Success)
					{
						if (int.TryParse(match.Groups["episodeNumber"].ValueSpan, out int tempEpisodeInt) == true)
						{
							episodeInt = tempEpisodeInt;
							title = title.Replace($": Part {episodeInt}", string.Empty);
						}
					}
				}
				else if (podcast.Name == "Wrestling With The Week")
				{
					if (guid == "54a6de4c-8139-11eb-9432-9f8052e41166")
					{
						// 2021-03-10 080000 - S01 E9 - BONUS EPISODE - EXCLUSIVE - Scorpio Sky is the Face of the Revolution. (54a6de4c-8139-11eb-9432-9f8052e41166).mp3
						episodeInt = -1;
					}
				}
					
			
				#endregion

				// ^(\d{4})-(\d{2})-(\d{2}) (\d{4}) - ([a-zA-Z0-9- !,_&"'\(\).#+\[\]$=íūÆōö@åéñÜá™öã]*) \(([a-z0-9]{8})-([a-z0-9]{4})-([a-z0-9]{4})-([a-z0-9]{4})-([a-z0-9]{12})\).mp3$\n


				if (episodeInt == -1)
				{
					var regex = new Regex(@"^(.*)#(?<episodeNumber>\d*)(\s*)\(([a-z0-9]{8})-([a-z0-9]{4})-([a-z0-9]{4})-([a-z0-9]{4})-([a-z0-9]{12})\).mp3");
					var match = regex.Match(title);
					if (match.Success)
					{
						var episodeNumber = match.Groups["episodeNumber"];
						Debugger.Break();
					}
				}


				// Prefix is empty to start of with.
				var episodeSeasonPrefix = String.Empty;

				// If there is a season number we use it.
				if (seasonInt >= 0)
				{
					// If there is an episode number we use it.
					if (episodeInt >= 0)
					{
						episodeSeasonPrefix = $"S{seasonInt:00} E{episodeInt} - ";
					}
					else
					{
						episodeSeasonPrefix = $"S{seasonInt:00} - ";
					}
				}
				else
				{
					// If there is no season number we just use the episode number if it exists.
					if (episodeInt >= 0)
					{
						episodeSeasonPrefix = $"E{episodeInt} - ";
					}
				}

				
				if (episodeInt == -1 && seasonInt == -1)
				{
					Log.Verbose($"No episode or season number found: {title}");
				}
				

				// Lets get rid of episodes with the number at the end, it isn't needed at
				// this point, because if we got here we have an E[number] to put at the start.
				if (episodeInt >= 0)
				{
					var stringTitleReplacements = new string[]
					{
						$" - {episodeInt}",
						$" - #{episodeInt}",
						$" -#{episodeInt}",
						$" #{episodeInt}",
						$" - #0{episodeInt}",
						$" #0{episodeInt}",
						$" # {episodeInt}",
						$" - [{episodeInt}]",
						$" [{episodeInt}]",
						$"- Episode {episodeInt}",
					};
					
					foreach (var stringTitleReplacement in stringTitleReplacements)
					{
						if (title.Contains(stringTitleReplacement))
						{
							title = title.Replace(stringTitleReplacement, string.Empty);
						}
					}

					// Remove any extra space
					title = title.Trim();
				}
				
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

				if (episodeInt == -1)
				{
					var containsEpisode = title.Contains("episode", StringComparison.OrdinalIgnoreCase);
					var containsSeason = title.Contains("season", StringComparison.OrdinalIgnoreCase);

					//(.*)-(\s*)#(\d*)$
					//(.*)Podcast #(\d*)$\n
					//(.*)-(\s*)#(\d*)(\s*)\n
					if (title.Contains("#"))
					{
						//Console.WriteLine(title);
						//Debugger.Break();
					}
				}
				
				var enclosureUri = new Uri(enclosureUriString);

				// Now we create the episode filename, this could be something like,
				// 2021-03-27 0155 - S4 E28 Ha, that good one (b8a112d6-4b8b-42f0-9389-d7d22419dc5f).mp3
				// 2021-03-28 0156 - E271 Yep that was good to (b8a112d6-4b8b-42f0-9389-d7d22419dc5f).mp3
				// 2021-03-29 1428 - That one where we all cried (b8a112d6-4b8b-42f0-9389-d7d22419dc5f).mp3
				var enclosureExtension = Path.GetExtension(enclosureUri.AbsolutePath);
				var episodeFilename = MakeSafeFilename($"{pubDate:yyyy-MM-dd HH:mm:ss} - {episodeSeasonPrefix}{title} ({guid}){enclosureExtension}");

				//Log.Information(episodeFilename);
				
				// This is the final path of where we save it.
				var episodePath = Path.Combine(podcastPath, episodeFilename);

				allFileNames.Add(episodePath);
				
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
					PodcastName = podcast.Name,
				};
				fileSummaryList.Add(fileSummary);

				PodcastEpisode podcastEpisode;
				try
				{
					podcastEpisode = db.Table<PodcastEpisode>().Single(x => x.PodcastName_Guid == $"{podcast.Name}_{guid}");

					//Debugger.Break();
				}
				catch (InvalidOperationException err) when (err.Message == "Sequence contains no matching element")
				{
					podcastEpisode = new PodcastEpisode(podcast.Name, guid)
					{
						DateAdded = DateTime.Now,
						FileName = Path.GetFileName(episodePath),
					};
					db.Insert(podcastEpisode);
					//Debugger.Break();
				}
				catch (Exception err)
				{
					Log.Error(err, "Could get or add item from SQLite database.");
					Debugger.Break();
					return 0;
				}
				

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

						// Backfill.
						if (podcastEpisode.Size == -1)
						{
							podcastEpisode.Size = fileInfo.Length;
							db.Update(podcastEpisode);
						}
					}
					else
					{
						// For some reason Black Box Down file size does not match actual file size.
						// So instead we just have to assume we did get all files downloaded correctly.
						//if (podcast.Name == "Black Box Down (FIRST Member Ad-Free)")
						//{
						//	shouldDownload = false;
						//}
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
			} // end of foreach (var item in items)



			var lockObject = new object();
			// We have now looked at every episode for this podcast. Now to download them all.
			// We use a Parallel.ForEachAsync so we can download them in parallel to speed things up.
			var parallelOptions = new ParallelOptions()
			{
				#if DEBUG
				MaxDegreeOfParallelism = 1,
				#else
				MaxDegreeOfParallelism = 8,
				#endif
			};
			await Parallel.ForEachAsync(fileSummaryList, parallelOptions, async (fileSummary, token) =>
			{
				// Only download if there is a remote URL.
				if (String.IsNullOrEmpty(fileSummary.RemoteUrl) == true)
				{
					return;
				}

				
				PodcastEpisode podcastEpisode;
				try
				{
					lock (lockObject)
					{
						podcastEpisode = db.Table<PodcastEpisode>().Single(x => x.PodcastName_Guid == $"{fileSummary.PodcastName}_{fileSummary.Guid}");
					}

					if (podcastEpisode.DateLastDownloaded != DateTime.MinValue)
					{
						var calculatedOutputPath = Path.Combine(archivePath, podcastEpisode.PodcastName, podcastEpisode.FileName);

						if (calculatedOutputPath == fileSummary.LocalFilename)
						{
							if (File.Exists(Path.Combine(archivePath, podcastEpisode.PodcastName, podcastEpisode.FileName)))
							{
								// Unless the episode was updated we don't even need to bother checking the headers.
								return;
							}
						}
					}
				}
				catch (Exception err)
				{
					Log.Error(err, "Could not load PodcastEpisode, it should always exist at this point.");
					Debugger.Break();
					return;
				}

				var episodeFilename = Path.GetFileName(fileSummary.LocalFilename);
				Log.Information($"Downloading {episodeFilename}");
				var fileName = Path.GetFileName(fileSummary.LocalFilename);
				var tempFileName = Path.Combine(tempDownloadsDirectory, fileName);
				try
				{
					using (var response = await _httpClient.GetAsync(fileSummary.RemoteUrl, HttpCompletionOption.ResponseHeadersRead, token))
					{
						response.EnsureSuccessStatusCode();

						// Sometimes the above checks fail us so we need to re-run them here. Checking ETag and content-length if they exist.

						var continueWithDownload = true;
						
						if (File.Exists(fileSummary.LocalFilename))
						{
							var eTagHeader = response.Headers.FirstOrDefault(x => x.Key.Equals("ETag", StringComparison.OrdinalIgnoreCase));
							var contentLength = response.Content.Headers.FirstOrDefault(x => x.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase));

							if (eTagHeader.Key is not null && eTagHeader.Value.Any())
							{
								var eTagValue = eTagHeader.Value.First();
								var md5Hash = string.Empty;
								using (var fileStream = File.OpenRead(fileSummary.LocalFilename))
								{
									using (var md5 = MD5.Create())
									{
										var hash = await md5.ComputeHashAsync(fileStream, token);
										md5Hash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
									}
								}

								if (string.IsNullOrEmpty(md5Hash) == false)
								{
									if (md5Hash == eTagValue)
									{
										continueWithDownload = false;
										Log.Information($"ETag ({md5Hash}) matches for {fileSummary.LocalFilename}, skipping.");
									}
								}
							}
							else if (contentLength.Key is not null && contentLength.Value.Any())
							{
								// If we get here the ETag exists and we use that instead.
								// If ETag does not exist then we use this to check content length as a secondary test.
								// Keeping in mind we already checked enclosureLength above, but sometimes it lies.
								string contentLengthValue = contentLength.Value.First() ?? "-1";

								var fileInfo = new FileInfo(fileSummary.LocalFilename);
								if (long.TryParse(contentLengthValue, out long contentLengthLong) == true)
								{
									if (fileInfo.Length == contentLengthLong)
									{
										continueWithDownload = false;
										Log.Information($"File size matches Content-Length ({fileInfo.Length}) for {fileSummary.LocalFilename}, skipping.");
									}
								}
							}
						}

						if (continueWithDownload)
						{
							// Use this stream to download the data.
							using (var fileStream = File.Create(tempFileName))
							{
								using (var stream = await response.Content.ReadAsStreamAsync(token))
								{
									await stream.CopyToAsync(fileStream, token);
								}
								await fileStream.FlushAsync(token);
								using (var md5 = MD5.Create())
								{
									var hash = await md5.ComputeHashAsync(fileStream, token);
									podcastEpisode.MD5Hash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
								}
								
								fileSummary.ActualLength = fileStream.Length;
								podcastEpisode.Size = fileStream.Length;
								podcastEpisode.DateLastDownloaded = DateTime.Now;
							}

							File.Move(tempFileName, fileSummary.LocalFilename, true);
							Log.Information($"Download success: {fileSummary.LocalFilename}");
						}
						else
						{
							// Backfill.
							if (podcastEpisode.DateLastDownloaded == DateTime.MinValue)
							{
								podcastEpisode.DateLastDownloaded = DateTime.Now;
							}
						}
						
						lock (lockObject)
						{
							db.Update(podcastEpisode);
						}
					}
				}
				catch (Exception err)
				{
					Log.Error(err, $"Could not download {episodeFilename}.");
					if (File.Exists(tempFileName))
					{
						try
						{
							File.Delete(tempFileName);
						}
						catch (Exception err2)
						{
							Log.Error(err2, $"Could not delete {tempFileName}.");

						}
						
					}
					//Debugger.Break();
				}
			});

			// Now that we have downloaded (or at least attempted to download) each of the episodes we
			// want to save a summary.json in the folder so we have a reference of what exists.
			var summaryJson = Path.Combine(podcastPath, "summary.json");
			BackupFile(summaryJson);
			using (var fileStream = File.Create(summaryJson))
			{
				JsonSerializer.Serialize(fileStream, fileSummaryList, new JsonSerializerOptions() { WriteIndented = true });
			}
		}
		allFileNames.Sort();

		var lastPodcastPath = String.Empty;
		var stringBuilder = new StringBuilder();

		var completedPodcasts = new List<string>()
		{
			/*
			"30 Morbid Minutes (FIRST Member Ad-Free)",
			"A Simple Talk (FIRST Member Ad-Free)",
			"Always Open (FIRST Member Ad-Free)",
			"ANMA (FIRST Member Ad-Free)",
			"Annual Pass (FIRST Member Ad-Free)",
			"Beneath (FIRST Member Ad-Free)",
			"Black Box Down (FIRST Member Ad-Free)",
			"D&D, but... (FIRST Member Ad-Free)",
			"DEATH BATTLE Cast (FIRST Member Ad-Free)",
			"F**kface (FIRST Member Ad-Free)",
			"Face Jam (FIRST Member Ad-Free)",
			"Funhaus Podcast (FIRST Member Ad-Free)",
			"Good Morning From Hell (FIRST Member Ad-Free)",
			"Hypothetical Nonsense (FIRST Member Ad-Free)",
			"Must Be Dice (FIRST Member Ad-Free)",	
			"Off Topic (FIRST Member Ad-Free)",
			"OT3 Podcast (FIRST Member Ad-Free)",
			"Red Web (FIRST Member Ad-Free)",
			"Rooster Teeth Podcast (FIRST Member Ad-Free)",
			"Ship Hits The Fan (FIRST Member Ad-Free)",
			"So... Alright (FIRST Member Ad-Free)",
			"Tales from the Stinky Dragon (FIRST Member Ad-Free)",
			"The Dogbark Podcast (FIRST Member Ad-Free)",
			"The Most (FIRST Member Ad-Free)",
			"Trash for Trash (FIRST Member Ad-Free)",
			
			"30 Morbid Minutes",
			"Always Open",
			"ANMA",
			"Annual Pass",
			"Beneath",
			"Black Box Down",
			"D&D, but...",
			"DEATH BATTLE Cast",
			"F**kface",
			"Face Jam",
			"Filmhaus Podcast",
			"Funhaus Podcast",
			"Glitch Please",
			"Good Morning From Hell",
			"Heroes & Halfwits",
			"Hypothetical Nonsense",
			"Inside Gaming Roundup",
			"Must Be Dice",
			"No Dumb Answers with Mark & Brad",
			"Off Topic",
			"On The Spot",
			"OT3 Podcast",
			"Red Web",
			"Relationship Goals",
			"Rooster Teeth Podcast",
			"RWBY Rewind",
			"Ship Hits The Fan",
			"So... Alright",
			"Tales from the Stinky Dragon",
			"Trash for Trash",
			"Twits and Crits",
			"unLOCKED - The Official genLOCK Companion Podcast",
			"The Most",
			"The Real Canon",
			"CHUMP",
			"Class Of 198X",
			"Enjoy the Show",
			"I Have Notes",
			"Fan Service",
			"Inside Gaming Daily",
			"Inside Gaming Podcast",
			"Murder Room",
			"Sportsball",
			"The Bungalow - The Business of Rooster Teeth",
			"The Patch",
			"Theater Mode AUX",
			"Twits and Crits - The League of Extraordinary Jiremen",
			"Wrestling With The Week",
			*/
		};
		
		foreach (var fileName in allFileNames)
		{
			var newFileName = fileName.Substring(archivePath.Length + 1);
			var indexOf = newFileName.IndexOf("/");
			var podcastName = newFileName.Substring(0, indexOf);
			var actualFileName = newFileName.Substring(indexOf + 1);

			if (completedPodcasts.Contains(podcastName))
			{
				continue;
			}
			
			if (podcastName != lastPodcastPath)
			{
				stringBuilder.AppendLine($"\n\n\n{podcastName}");
				lastPodcastPath = podcastName;
			}

			stringBuilder.AppendLine(actualFileName);
			//Debugger.Break();
		}
		
		var allmp3sSummary = Path.Combine(archivePath, "all_mp3s.txt");
		BackupFile(allmp3sSummary);
		File.WriteAllText(allmp3sSummary, stringBuilder.ToString());
		//Console.WriteLine(String.Join("\n", allFileNames));
		return 0;
	}

	static int GuidStrToInt(string guid)
	{
		if (guid.Length != 36)
		{
			return -1;
		}
		
		var guidArray = Guid.Parse(guid).ToByteArray();
		var intBytes = new byte[4];
		Array.Copy(guidArray, guidArray.Length - 4, intBytes, 0, intBytes.Length);
		return BitConverter.ToInt32(intBytes);
	}
}

