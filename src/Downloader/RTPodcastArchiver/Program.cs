using System;
using System.CommandLine;
using System.CommandLine.Invocation;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
			var backupPath = Path.Combine(Path.GetDirectoryName(fileName), newFileName);
			File.Move(fileName, backupPath);
		}
	}


	static async Task<int> RunAsync(string outputPath)
	{
		var loadPodcastsFromCache = true;
		
		_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4.1 Safari/605.1.15");
			
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
			
			podcasts.Add(new Podcast("30 Morbid Minutes"));
			podcasts.Add(new Podcast("A Simple Talk"));
			podcasts.Add(new Podcast("Always Open"));
			podcasts.Add(new Podcast("ANMA"));
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
			podcasts.Add(new Podcast("Off Topic"));
			podcasts.Add(new Podcast("OT3 Podcast"));
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

		
		var allFileNames = new List<string>();
		
		// Go through each podcast.
		foreach (var podcast in podcasts)
		{
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
				
			}
			else
			{
				// Download podcast manifest.
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

				if (podcast.Name == "30 Morbid Minutes")
				{
					if (guid == "49392c9a-e5ff-11ed-9fdc-13177201a8ae")
					{
						// 2023-05-01 0700 - S05 E142 - The Screaming Mummy (49392c9a-e5ff-11ed-9fdc-13177201a8ae).mp3
						episodeInt = 42;
					}
					else if (guid == "68d2f436-a5ae-11ee-ba8e-0bea39a3a468")
					{
						// 2024-01-02 0800 - E69 - Inside Body Farms with a Future Resident (68d2f436-a5ae-11ee-ba8e-0bea39a3a468).mp3
						seasonInt = 8;
					}
				}
				else if (podcast.Name == "A Simple Talk")
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
					if (guid == "b9903262-16de-11ee-9d10-23c3320b495e")
					{
						// 2023-07-10 1300 - E19 - Does Exercise ACTUALLY help your Brain - Always Open (b9903262-16de-11ee-9d10-23c3320b495e).mp3
						seasonInt = 7;
						episodeInt = 159;
					}
				}
				else if (podcast.Name == "ANMA")
				{
					// Unsure what should be done to these episodes, so they are left as is.
					// 2022-06-19 0700 - E7 - The Beginning of Our Internet Journey (7346c710-ee64-11ec-b323-7b2bc1fab113).mp3
					// 2022-06-26 0700 - E2 - Convention Memories (41443606-f406-11ec-bc8e-ffe149b2963c).mp3
					// 2022-07-03 0700 - E1 - Geoff & Eric at Vidcon (b28ff310-f891-11ec-bea6-537e621e57de).mp3
					// 2022-07-10 0700 - E2 - LEAKED ANMA RTX Panel Live (11be2d24-ff0a-11ec-b67d-bb90bacbeb66).mp3
					// 2022-07-17 0700 - E9 - Gus Gets Puked On (0d0b9b12-0532-11ed-98d9-abd4c691f5bb).mp3
					if (guid == "32899d96-c1fe-11ee-b94e-cf2d0af2da17")
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
					if (guid == "94f93eba-d288-11ee-941f-dfda8057d242")
					{
						// 2024-02-26 0800 - S03 E75 - Third Wave Coffee (94f93eba-d288-11ee-941f-dfda8057d242).mp3
						seasonInt = -1;
					}
					else if (guid == "ce2cb668-dd77-11ee-987d-c7d840b52aa7")
					{
						// 2024-03-11 0700 - S03 E77 - It's a Mayonnaise Commercial (ce2cb668-dd77-11ee-987d-c7d840b52aa7).mp3
						seasonInt = -1;
					}
					else if (guid == "61fb6918-e87a-11ee-b4cb-a769e3c22fab")
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
				else if (podcast.Name == "Annual Pass")
				{
					// Perfect, no notes.
				}
				else if (podcast.Name == "Beneath")
				{
					// Perfect, no notes.
				}
				else if (podcast.Name == "Black Box Down")
				{
					// Unsure what is going on here
					// 2022-05-18 0700 - S08 E81 - All About Airspaces, The Places You Can't Fly In (62c3adc6-d55b-11ec-a86b-9777fb213db6).mp3
					// 2022-06-01 0700 - S08 E82 - Leaving Luggage in an Airport feat. Geoff Ramsey, Chris Left His Pants on a Plane (08c2be3c-e124-11ec-a968-4f01a79d1415).mp3
					// 2022-06-08 0700 - S09 E83 - Was This Crash an Assassination, Polish Air Force Flight 101 Crashes with President on Board (0ef203be-e67c-11ec-8aef-b77fc2d6046c).mp3
					// 2022-06-15 0700 - S09 E82 - Pilots Struggle as Airplane Nosedives, Alaska Airlines Flight 261 Crashes off California Coast (4f804628-ec23-11ec-8be6-3381e2e6d57d).mp3
					// 2022-06-22 0700 - S09 E83 - Pilots Accidently Cause a Go Around, China Airlines Flight 140 Crashes At Airport (38443122-f1b9-11ec-b72e-5f43ca3dec9c).mp3
					// 2022-06-29 0700 - S09 E84 - Airplane Does a Barrel Roll with Passengers on Board, American Eagle Flight 4184 Loses Control (3b72abd8-f724-11ec-a0f0-e3999ce6abe8).mp3
					// 2022-07-06 0700 - S09 E85 - Did The Pilot Crash This Plane on Purpose, EgyptAir Flight 990 Ends In Controversy (772183da-fcc3-11ec-8e0a-7f50c8d3e3a5).mp3

					// Our Scariest Airplane Moments! might be supplemental?
					// 2022-07-20 070000 - S09 E87 - Airplane Crashes In A Mall Parking Lot, Fine Air Flight 101 Crashes in a Mall Parking Lot (7b370f18-077b-11ed-b86a-77c1fb57d26e).mp3
					// 2022-07-27 070000 - S09 E88 - A Reset Circuit Breaker Causes Crash, Indonesia AirAisia Flight 8501 Falls to the Sea (07635c50-0cfc-11ed-9eb0-9bbf717650e1).mp3
					// 2022-08-03 070000 - S09 E88 - Our Scariest Airplane Moments!, First Class (7d2f82e0-12ad-11ed-b5d8-cb167f08ab4c).mp3
					// 2022-08-03 070000 - S09 E89 - A Failure the Pilots Were Never Trained For, TAM Flight 402 Crashes into São Paulo (b8974458-12c6-11ed-8ca5-2f8aabcba2ed).mp3

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
				}
				else if (podcast.Name == "D&D, but...")
				{
					// Perfect, no notes.
				}
				else if (podcast.Name == "DEATH BATTLE Cast")
				{
					// Looking at title for episode number, this says #80, but its the 60th in the list
					// 2018-06-09 1900 - Strange vs Fate Sneak Peak - #80 (00000000-0000-0000-0000-000050000000).mp3

					// Missing episode 243
					// 2021-08-20 1700 - E241 - Gru VS Megamind (31ecfa82-012a-11ec-85da-3b7ac45d7395).mp3
					// 2021-08-27 1700 - E242 - Osmosis Jones VS White Blood Cell U-1146 (cd59d882-068e-11ec-baf6-6fe9a1d72f4b).mp3
					// 2021-09-16 1700 - E245 - Emperor Joker VS God Kefka (4364efde-16f8-11ec-a28e-d33484d4949e).mp3
					// 2021-09-23 1700 - E246 - Wario VS Knuckles (eadaf0d2-1c2b-11ec-a94c-b745395eff61).mp3

					// We started getting S08
					if (guid == "0b5dd28c-bddb-11ed-86b3-8ba8aa384f5c")
					{
						// 2023-03-09 1800 - E321 - Tier Ranking ULTIMATE Female Villains (0b5dd28c-bddb-11ed-86b3-8ba8aa384f5c).mp3
						seasonInt = 8;
					}
					else if (guid == "9ab71e6c-c35c-11ed-b8ad-b7e86d44988a")
					{
						// 2023-03-16 1700 - E322 - Deku VS Gon (9ab71e6c-c35c-11ed-b8ad-b7e86d44988a).mp3
						seasonInt = 8;
					}
					else if (guid == "129062f8-c8f9-11ed-962a-8f5b56686fc8")
					{
						// 2023-03-23 1700 - E323 - Nemesis VS Pyramid Head (129062f8-c8f9-11ed-962a-8f5b56686fc8).mp3
						seasonInt = 8;
					}
					else if (guid == "ac1a70be-1b8b-11ee-88ef-d7d5640237f0")
					{
						// 2023-07-06 1700 - E338 - Captain Britain (Marvel) vs Uncle Sam (DC) (ac1a70be-1b8b-11ee-88ef-d7d5640237f0).mp3
						seasonInt = 8;
					}
				}
				else if (podcast.Name == "F**kface")
				{
					if (guid == "5a763164-9088-11ed-a11b-9372f9497016")
					{
						// 2023-01-10 0800 - E136 - Andrew is On Your Side - Geoff's year old Cosmic Crisp (5a763164-9088-11ed-a11b-9372f9497016).mp3
						seasonInt = 5;
					}
					else if (guid == "a07ecb2e-572c-11ee-81a6-b765bc79ff5f")
					{
						// 2023-09-20 0700 - E172 - Cock Money - Punchlines (a07ecb2e-572c-11ee-81a6-b765bc79ff5f).mp3
						seasonInt = 6;
					}
					else if (guid == "e6b5be60-5c93-11ee-b21e-c77334f025aa")
					{
						// 2023-09-27 0700 - E173 - Andrews Ankles - Regulation Flavors (e6b5be60-5c93-11ee-b21e-c77334f025aa).mp3
						seasonInt = 6;
					}
					else if (guid == "670e1858-620e-11ee-b5c5-a3ef96ca8d9d")
					{
						// 2023-10-04 0700 - E174 - Caviar Phones - Internal Monologues (670e1858-620e-11ee-b5c5-a3ef96ca8d9d).mp3
						seasonInt = 6;
					}
					else if (guid == "03d6e61c-679e-11ee-a7ba-0fbfecebf62d")
					{
						// 2023-10-11 0700 - E175 - Baby Alien Schlongs - Sleep Hacks (03d6e61c-679e-11ee-a7ba-0fbfecebf62d).mp3
						seasonInt = 6;
					}
					else if (guid == "19e1229e-6d19-11ee-bd21-f79bbe91ead3")
					{
						// 2023-10-18 0700 - E176 - Tomorrow Is Chores - Naughty Naked Video Games  (19e1229e-6d19-11ee-bd21-f79bbe91ead3).mp3
						seasonInt = 6;
					}
					else if (guid == "d0deef90-729a-11ee-aff4-83b381d8c635")
					{
						// 2023-10-25 0700 - E177 - Appropriate Squirts - Key West Bachelorette Weekend (d0deef90-729a-11ee-aff4-83b381d8c635).mp3
						seasonInt = 6;
					}
					else if (guid == "93f7faec-9ebc-11ee-b0ab-47000b1f6dca")
					{
						// 2023-12-20 0800 - E185 - It's Scary Out There - Wheel of Years (93f7faec-9ebc-11ee-b0ab-47000b1f6dca).mp3
						seasonInt = 6;
					}
					else if (guid == "73e79d30-9ec5-11ee-b6d7-639b5cea62fd")
					{
						// 2023-12-27 0800 - E186 - Assholes and Ice Skates - Fart Drama (73e79d30-9ec5-11ee-b6d7-639b5cea62fd).mp3
						seasonInt = 6;
					}
					else if (guid == "7717ac22-a9b3-11ee-8a77-a7b28706223d")
					{
						// 2024-01-03 0800 - E187 - Getting Our Dicks Wet In The New Year - Signs from Howard Stern (7717ac22-a9b3-11ee-8a77-a7b28706223d).mp3
						seasonInt = 6;
					}
					else if (guid == "b30cf3e6-c541-11ee-9167-67f3db2cf565")
					{
						// 024-02-07 0800 - E192 - In The Owl City Lab - Andrew Got A Cock (b30cf3e6-c541-11ee-9167-67f3db2cf565).mp3
						seasonInt = 6;
					}
					else if (guid == "badc4148-cabf-11ee-acb6-e7ccad1541a1")
					{
						// 2024-02-14 0800 - E193 - Buying a Mini Blimp - Naked Floppy Running (badc4148-cabf-11ee-acb6-e7ccad1541a1).mp3
						seasonInt = 6;
					}
					else if (guid == "93169270-d03d-11ee-9fa6-6790a3e1de76")
					{
						// 2024-02-21 0800 - E194 - Small Dick Mode - 8 Minute Tub Time (93169270-d03d-11ee-9fa6-6790a3e1de76).mp3
						seasonInt = 6;
					}
					else if (guid == "e20e8052-d5bc-11ee-bc84-43392ba46b40")
					{
						// 2024-02-28 0800 - E195 - Gavin is Here for Pleasantries - Season 2022 (e20e8052-d5bc-11ee-bc84-43392ba46b40).mp3
						seasonInt = 6;
					}
					else if (guid == "d6c1c52c-db3b-11ee-acde-ebc74a1073b5")
					{
						// 2024-03-06 0800 - E197 - Fidget Guns and Monster Trucks - Death of Umidigi (d6c1c52c-db3b-11ee-acde-ebc74a1073b5).mp3
						seasonInt = 6;
					}
					else if (guid == "11eee356-e63a-11ee-9b99-cbe1235ff40e")
					{
						// 2024-03-20 0700 - E199 - Discount Pranking - Farts In Written Word (11eee356-e63a-11ee-9b99-cbe1235ff40e).mp3
						seasonInt = 6;
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
					
					if (guid == "5917b87c-ebb5-11ee-b8d2-7fd3607e71a1")
					{
						// Season 6 still?
						// 2024-03-27 070000 - E200 - Alabama Poutine - The Perpetual Food Truck (5917b87c-ebb5-11ee-b8d2-7fd3607e71a1).mp3
						seasonInt = 6;
					}
				}
				else if (podcast.Name == "Face Jam")
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
					
					// Unsure what is episode 102
					// 2023-09-26 070000 - E101 - Quiznos Big Fat Greek Sub (5fabc6d2-5bb5-11ee-9717-ffab0ae9fb2a).mp3
					// 2023-09-27 070000 - Ride Along - Quiznos (e113879c-5beb-11ee-82ba-07d7493fd6a9).mp3
					// 2023-09-28 070000 - Cat-ering - Quiznos (72c83f88-5bf2-11ee-88dd-aff3eab2f7c5).mp3
					// 2023-10-03 070000 - Spittin Silly - Frozen Pizza Challenge (3cc7b29a-613f-11ee-b96b-07a2922df77f).mp3
					// 2023-10-05 150000 - Pringles Taste Test (f11ea1ec-6241-11ee-b4ae-8ba61eecdff2).mp3
					// 2023-10-10 070000 - Fazoli's Pizza Baked Pasta (784aa38c-66bb-11ee-a410-8b65cac3ade1).mp3
					// 2023-10-11 150000 - Ride Along - Fazoli's (7094ad70-678f-11ee-ba2a-9f500cba01de).mp3
					// 2023-10-17 070000 - Spittin Silly - Chuck E Cheese Pizza Comparison (8f4b39b2-69f3-11ee-97d7-7f3e9fbc20ce).mp3
					// 2023-10-24 070000 - E103 - Potbelly Ring of Fire Sandwich (7c7e50f0-6f5f-11ee-a708-c711c79df990).mp3
					
					if (guid == "b1863dea-5783-11eb-9b78-47c1baa1c26f")
					{
						//2021-01-19 080000 - Dairy Queen Rotisserie-Style Chicken Bites & Brownie Dough Blizzard (b1863dea-5783-11eb-9b78-47c1baa1c26f).mp3
						episodeInt = 32;
					}
					else if (guid == "369843e2-6d88-11eb-b9aa-8ba38ba51dca")
					{
						// 2021-02-16 080000 - Red Lobster Wagyu Bacon Cheeseburger (369843e2-6d88-11eb-b9aa-8ba38ba51dca).mp3
						episodeInt = 34;
					}
					else if (guid == "36096f46-9bf0-11eb-a233-0771cf9cf0f8")
					{
						// 2021-04-13 070000 - TGI Fridays Under the Big Top Menu (36096f46-9bf0-11eb-a233-0771cf9cf0f8).mp3
						episodeInt = 38;
					}
					else if (guid == "458ea660-4690-11ed-b73d-776979bb74a2")
					{
						// 2022-10-10 070000 - Chili's Signature Bar Menu (458ea660-4690-11ed-b73d-776979bb74a2).mp3
						episodeInt = 77;
					}
					else if (guid == "5c405924-a140-11ee-92c4-d703c4298a13")
					{
						// 2024-01-02 080000 - Chuck E Cheese Grown Up Menu (5c405924-a140-11ee-92c4-d703c4298a13).mp3
						episodeInt = 108;
					}
					else if (guid == "57bc256c-2dfc-11ed-87b4-47a32d663c1e")
					{
						// 2022-09-06 161000 - E1 - Spittin Silly - Theme Song (57bc256c-2dfc-11ed-87b4-47a32d663c1e).mp3
						episodeInt = -1;
					}
					else if (guid == "fe86efb8-ae47-11ee-b0da-d389f0120b00")
					{
						// 2024-01-09 080000 - E36 - Spittin Silly - Freewheelin (fe86efb8-ae47-11ee-b0da-d389f0120b00).mp3
						episodeInt = -1;
					}

				}
				else if (podcast.Name == "Funhaus Podcast")
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

					// Later we swap from season episode to global episode number.
					// 2022-12-07 1400 - S08 E47 - Live from Our Brand New Completely Empty Studio! - Funhaus Podcast (f5fc7ef4-75ee-11ed-8557-2b8ca9348681).mp3
					// 2022-12-14 1400 - S08 E408 - All We Want for Christmas is Death Stranding 2 - Funhaus Podcast (993a5764-7b7e-11ed-ae45-3fcfb184407a).mp3
					
				}
				else if (podcast.Name == "Good Morning From Hell")
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
				else if (podcast.Name == "Hypothetical Nonsense")
				{
					if (guid == "602f6064-597b-11ee-b43d-c75f60d46582")
					{
						// 2023-09-25 2000 - TAKING A HIT OF THE SHAQ PIPE (602f6064-597b-11ee-b43d-c75f60d46582).mp3
						episodeInt = 4;
					}
					else if (guid == "37a28184-6494-11ee-ab26-87b731b98b89")
					{
						//2023-10-09 200000 - E4 - Relationship Red Flags (37a28184-6494-11ee-ab26-87b731b98b89).mp3
						episodeInt = 6;
					}
					else if (guid == "a0a57490-88a5-11ee-b124-eb775b6270b5")
					{
						// 2023-11-27 210000 - E11 - The ULTIMATE Road Trip (a0a57490-88a5-11ee-b124-eb775b6270b5).mp3
						episodeInt = 13;
					}
				}
				else if (podcast.Name == "Must Be Dice")
				{
					if (guid == "855db4d4-c2bf-11ec-a890-778d01c8fd9d")
					{
						// 2022-04-24 0700 - Stranger Than Stranger Things - Paradise Path RPG Ep 1 (855db4d4-c2bf-11ec-a890-778d01c8fd9d).mp3
						seasonInt = 1;
						episodeInt = 1;
					}
					else if (guid == "15cb58ea-c843-11ec-843f-e77608c59636")
					{
						// 2022-05-01 0700 - Trouble in Paradise - Paradise Path RPG Ep 2 (15cb58ea-c843-11ec-843f-e77608c59636).mp3
						seasonInt = 1;
						episodeInt = 2;
					}
					else if (guid == "d65fa1d8-cda2-11ec-b43f-ef5b8f967939")
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
					else if (guid == "7974e882-4cb1-11ed-bba0-9fd4b0fa31d0")
					{
						// 2022-10-17 1300 - S02 E1 - Play, You Fools! - Super Princess Rescue Quest RPG Ep 1 (7974e882-4cb1-11ed-bba0-9fd4b0fa31d0).mp3
						episodeInt = 11;
					}
					else if (guid == "54143a44-599e-11ed-9e1b-dba62af9d44b")
					{
						// 2022-11-01 0700 - S02 E2 - A Hero Will Fall - Super Princess Rescue Quest RPG Ep 2 (54143a44-599e-11ed-9e1b-dba62af9d44b).mp3
						episodeInt = 12;
					}
					else if (guid == "9b71d468-5f2b-11ed-96c4-7fb5835ff94a")
					{
						// 2022-11-08 1400 - S02 E3 - Last Rites of the Great Frog King - Super Princess Rescue Quest RPG Ep 3 (9b71d468-5f2b-11ed-96c4-7fb5835ff94a).mp3
						episodeInt = 13;
					}
					else if (guid == "0da09b8e-6222-11ed-8edd-676a38c78e3a")
					{
						// 2022-11-14 1400 - S02 E4 - We Hold the Frog King's Oath Fulfilled - Super Princess Rescue Quest RPG Ep 4 (0da09b8e-6222-11ed-8edd-676a38c78e3a).mp3
						episodeInt = 14;
					}
					else if (guid == "87b6f39a-6831-11ed-b461-f7ecba4f8cfc")
					{
						// 2022-11-21 1400 - S02 E5 - To Die Fighting Side By Side with a Rat - Super Princess Rescue Quest RPG Ep 5 (87b6f39a-6831-11ed-b461-f7ecba4f8cfc).mp3
						episodeInt = 15;
					}
					else if (guid == "7ebdb894-6bb2-11ed-8f04-db104c00088f")
					{
						// 2022-11-28 1400 - S02 E6 - Taking the Battle to the Skies - Super Princess Rescue Quest RPG Ep 6 (7ebdb894-6bb2-11ed-8f04-db104c00088f).mp3
						episodeInt = 16;
					}
					else if (guid == "b6ec7bac-72cd-11ed-8ffb-e383de1638ba")
					{
						// 2022-12-05 1400 - S02 E7 - Neville's Meat is Back on the Menu - Super Princess Rescue Quest RPG Ep 7 (b6ec7bac-72cd-11ed-8ffb-e383de1638ba).mp3
						episodeInt = 17;
					}
					else if (guid == "d71ffb4a-7a54-11ed-8082-b7c62a3d5e51")
					{
						// 2022-12-12 2000 - S02 E19 - Flogging the Cyclops - Super Princess Rescue Quest RPG Ep 8 (d71ffb4a-7a54-11ed-8082-b7c62a3d5e51).mp3
						episodeInt = 18;
					}
				}
				else if (podcast.Name == "Off Topic")
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
				else if (podcast.Name == "OT3 Podcast")
				{
					if (guid == "2e6ae14c-0c35-11ec-87f2-bf6f978fb24d")
					{
						// 2021-09-03 0700 - What Fanfiction Trope Are You (2e6ae14c-0c35-11ec-87f2-bf6f978fb24d).mp3
						episodeInt = 8;
						seasonInt = 2;
					}
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
				}
				else if (podcast.Name == "Red Web")
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
					else if (guid == "3c1df836-5c38-11eb-90b9-8323a659fca3")
					{
						// Is more of a supp episode. 
						// 2021-01-22 080000 - S02 E24 - Red Web Radio (3c1df836-5c38-11eb-90b9-8323a659fca3).mp3
						episodeInt = -1;
					}
				}
				else if (podcast.Name == "Rooster Teeth Podcast")
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

					if (guid == "8946ad28-e31d-11ee-a590-175b7df13273")
					{
						// 2024-03-18 190000 - E794 - The Rooster Teeth Podcast Live (8946ad28-e31d-11ee-a590-175b7df13273).mp3
						episodeInt = 793;
					}
					else if (guid == "7f23714c-e871-11ee-8888-836dbfd571bc")
					{
						// 2024-03-25 190000 - E793 - Venmo vs Cashapp (7f23714c-e871-11ee-8888-836dbfd571bc).mp3
						episodeInt = 794;
					}
				}
				else if (podcast.Name == "Ship Hits The Fan")
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
				else if (podcast.Name == "So... Alright")
				{
					if (guid == "7316ad24-4e75-11ee-9051-bb3bc7db730a")
					{
						// 2023-09-04 0700 - S01 E2 - I Know Who Shot JR (7ba3cf0a-48f6-11ee-80e7-cbef7e41727b).mp3
						// 2023-09-11 0700 - S01 E4 - I saw dead people (7316ad24-4e75-11ee-9051-bb3bc7db730a).mp3
						// 2023-09-19 0700 - S01 E4 - This One Goes to 11 (7b8d7400-531c-11ee-8155-6fa1067b4a18).mp3
						episodeInt = 3;
					}
					else if (guid == "213367dc-8254-11ee-b7d8-7798dd0fd8d5")
					{
						// E12 - Fantastic Man (213367dc-8254-11ee-b7d8-7798dd0fd8d5).mp3
						seasonInt = 1;
					}
					else if (guid == "78f7be26-c473-11ee-a2d7-17bf8060e49d")
					{
						// E24 - Ephemeral Ants (78f7be26-c473-11ee-a2d7-17bf8060e49d).mp3
						seasonInt = 2;
					}
					else if (guid == "c9ff103a-eaed-11ee-be0f-9f74d46fd564")
					{
						// 2024-03-26 070000 - E31 - Mall Thoughts (c9ff103a-eaed-11ee-be0f-9f74d46fd564).mp3
						seasonInt = 2;
					}
				}
				else if (podcast.Name == "Tales from the Stinky Dragon")
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
				else if (podcast.Name == "The Dogbark Podcast")
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
				else if (podcast.Name == "The Most")
				{
					// two episode 49s, is one a sup?
					// 2022-01-30 080000 - S02 E48 - Fabulous Abacus (36606c00-8070-11ec-897a-dfa38e54a802).mp3
					// 2022-02-06 080000 - S02 E49 - Don't Feed The Bears (11036ae6-85e0-11ec-bd03-57b9439edf8d).mp3
					// 2022-02-13 080000 - S02 E49 - We Like The Slop (19f10ce6-8b92-11ec-bee3-bfa285442452).mp3
					// 2022-02-20 080000 - S02 E50 - Now That's What I Call Monsters (5eed4a04-90ff-11ec-9611-f31056cb5949).mp3
				}
				else if (podcast.Name == "Trash for Trash")
				{
					// Perfect, no notes.
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

					Console.WriteLine(title);
				}





				// Lets get rid of episodes with the number at the end, it isn't needed at
				// this point, because if we got here we have an E[number] to put at the start.
				if (episodeInt >= 0)
				{
					var stringTitleReplacements = new string[]
					{
						$" - {episodeInt}",
						$" - #{episodeInt}",
						$" #{episodeInt}",
						$" - [{episodeInt}]",
						$" [{episodeInt}]",
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
				
				// TODO: If episode number is not found we should try get it from the title " - #1"
				
				
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
						if (podcast.Name == "Black Box Down")
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
			} // end of foreach (var item in items)
            
			
			
			/*
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
					var fileName = Path.GetFileName(fileSummary.LocalFilename);
					var tempFileName = Path.Combine(Path.GetTempPath(), fileName);
					// Use this stream to download the data.
					using (var stream = await _httpClient.GetStreamAsync(fileSummary.RemoteUrl))
					{
						using (var fileStream = File.Create(tempFileName))
						{
							await stream.CopyToAsync(fileStream);
							fileSummary.ActualLength = fileStream.Length;
						}
						File.Move(tempFileName, fileSummary.LocalFilename);
					}
				}
				catch (Exception err)
				{
					Log.Information(err, $"Could not download {episodeFilename}.");
					Debugger.Break();
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
			*/
		}
		allFileNames.Sort();

		var lastPodcastPath = String.Empty;
		var stringBuilder = new StringBuilder();

		var completedPodcasts = new List<string>()
		{
			/*
			"30 Morbid Minutes",
			"A Simple Talk",
			"Always Open",
			"ANMA",
			"Annual Pass",
			"Beneath",
			"Black Box Down",
			"D&D, but...",
			"DEATH BATTLE Cast",
			"F**kface",
			"Face Jam",
			"Funhaus Podcast",
			"Good Morning From Hell",
			"Hypothetical Nonsense",
			"Must Be Dice",	
			"Off Topic",
			"OT3 Podcast",
			"Red Web",
			"Rooster Teeth Podcast",
			"Ship Hits The Fan",
			"So... Alright",
			"Tales from the Stinky Dragon",
			"The Dogbark Podcast",
			"The Most",
			"Trash for Trash",
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
		
		File.WriteAllText("test_files.txt", stringBuilder.ToString());
		//Console.WriteLine(String.Join("\n", allFileNames));
		return 0;
	}
}

