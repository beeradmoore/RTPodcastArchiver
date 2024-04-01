using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices.JavaScript;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using RTInternetArchiveUploader;
using RTPodcastArchiver;
using RunProcessAsTask;
using Serilog;


class Program
{
	static HttpClient _httpClient = new HttpClient();

	static async Task<int> Main(string[] args)
	{
		var rootCommand = new RootCommand("Uploads Rooster Teeth FIRST podcast archive to the Internet Archive.\nNOTE: You likely don't want to do this as there are already copies on Internet Archive.\nUse this repository/application for educational purposes only.");
		var inputOption = new Option<string>(new string[] { "--input", "-i" }, () =>
		{
			var envRTPodcastArchiverPath = Environment.GetEnvironmentVariable("RT_PODCAST_ARCHIVER_PATH") ?? String.Empty;
			if (String.IsNullOrEmpty(envRTPodcastArchiverPath) == false)
			{
				return envRTPodcastArchiverPath;
			}

			return String.Empty;
		}, "The input folder path. The archive exists within this property, eg. {input}/archive/ . You can also set the environment variable RT_PODCAST_ARCHIVER_PATH and omit input path to accomplish the same thing.");
		inputOption.IsRequired = true; // Doesn't work as we define 
		rootCommand.AddOption(inputOption);
		rootCommand.SetHandler(RunAsync, inputOption);

		return await rootCommand.InvokeAsync(args);
	}

	static async Task<int> RunAsync(string inputPath)
	{
		if (Directory.Exists(inputPath) == false)
		{
			Console.WriteLine("ERROR: Input path is not found, did you set it?");
			return 1;
		}

		var archivePath = String.Empty;
		var logPath = String.Empty;
		try
		{
			archivePath = Path.Combine(inputPath, "archive");
			if (Directory.Exists(archivePath) == false)
			{
				Log.Information($"ERROR: Archive path not found at {archivePath}");
				return 1;
			}

			logPath = Path.Combine(inputPath, "logs");
			if (Directory.Exists(inputPath) == false)
			{
				Directory.CreateDirectory(inputPath);
			}
		}
		catch (Exception err)
		{
			Log.Information($"ERROR: Could not setup initial folders. ({err.Message})");
			return 1;
		}



		// Setup logger
		Log.Logger = new LoggerConfiguration()
			.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss}] {Message}{NewLine}{Exception}")
			.WriteTo.File(Path.Combine(logPath, "rt_podcast_archive_uploader_.log"), rollingInterval: RollingInterval.Day)
			.CreateLogger();


		// First make sure ia is installed.
		try
		{
			var processResults = await ProcessEx.RunAsync("ia", "--version");
			if (processResults.ExitCode != 0)
			{
				throw new Exception("Could not get ia version.");
			}

			// Could make sure we have the version number, but this should be fine.
			Log.Information($"Using ia v{processResults.StandardOutput[0]}");
		}
		catch (Exception err)
		{
			Log.Information("Error: ia (internet archive command-line tool) not found. Are you sure it is installed?");
			Log.Information(err.Message);
			return 1;
		}

		// We do web requests with accessKey and secretKey for the IA-s3 like API.
		// If you have not configured ia please see https://archive.org/developers/internetarchive/quickstart.html
		// We will attempt to load them below, but feel free to skip that by specifying them here.
		var accessKey = Environment.GetEnvironmentVariable("IAS3_ACCESS_KEY") ?? String.Empty;
		var secretKey = Environment.GetEnvironmentVariable("IAS3_SECRET_KEY") ?? String.Empty;

		if (String.IsNullOrEmpty(accessKey) == true || String.IsNullOrEmpty(secretKey) == true)
		{
			// Is it set as an environment variable?
			var iaConfigFile = Environment.GetEnvironmentVariable("IA_CONFIG_FILE") ?? String.Empty;
			if (String.IsNullOrEmpty(iaConfigFile))
			{
				// What if we guess the path?
				var homePath = ((Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX)
					? Environment.GetEnvironmentVariable("HOME")
					: Environment.ExpandEnvironmentVariables("%HOMEDRIVE%%HOMEPATH%")) ?? String.Empty;

				if (String.IsNullOrEmpty(homePath))
				{
					Log.Information("Could not get home path.");
					return 1;
				}

				var tempIaConfigFile = Path.Combine(homePath, ".config", "internetarchive", "ia.ini");
				if (File.Exists(tempIaConfigFile))
				{
					iaConfigFile = tempIaConfigFile;
				}
			}

			if (String.IsNullOrEmpty(iaConfigFile) == true || File.Exists(iaConfigFile) == false)
			{
				Log.Information("Unable to load your ia.ini file. You can avoid this if you hard code your access_key and secretKey in the source code itself.");
				return 1;
			}

			var iaConfig = File.ReadAllText(iaConfigFile) ?? String.Empty;

			var regex = new System.Text.RegularExpressions.Regex(@"^\[s3\]\naccess(\s*)=(\s*)(?<access>.*)\nsecret(\s*)=(\s*)(?<secret>.*)\n");
			var match = regex.Match(iaConfig);
			if (match.Success)
			{
				accessKey = match.Groups["access"].Value;
				secretKey = match.Groups["secret"].Value;
			}
			else
			{
				Log.Information($"Unable to load your access or secret from {iaConfigFile}");
				Log.Information("We use a regex that is specifically looking for");
				Log.Information("[s3]");
				Log.Information("access = abc");
				Log.Information("secret = 123");
				Log.Information("If you are using a non standard config file you can try setting accessKey and secretKey in the source itself.");
			}
		}

		if (String.IsNullOrEmpty(accessKey) == true || String.IsNullOrEmpty(secretKey) == true)
		{
			Log.Information("Can't continue without an accessKey or secretKey");
			return 1;
		}

		// Some nonsense when I was trying to fix timeouts.
		//var socketsHttpHandler = new SocketsHttpHandler();
		//socketsHttpHandler.ConnectTimeout = TimeSpan.FromMinutes(60);
		//socketsHttpHandler.Expect100ContinueTimeout = TimeSpan.FromMinutes(60);
		//socketsHttpHandler.ResponseDrainTimeout = TimeSpan.FromMinutes(60);
		//socketsHttpHandler.KeepAlivePingTimeout = TimeSpan.FromMinutes(60);
		//socketsHttpHandler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(60);


		// Can't use AllowAutoRedirect from HttpClientHandler as it does not redirect
		// from https to http which is what IA seems to do.
		var httpClient = new HttpClient();
		httpClient.Timeout = TimeSpan.FromMinutes(15);

		// This is where we used the accessKey and secretKey from above.
		httpClient.DefaultRequestHeaders.Add("authorization", $"LOW {accessKey}:{secretKey}");

		// podcasts.json is what we use to map local files to Internet Archive archives.
		var podcastsJson = Path.Combine(inputPath, "podcasts.json");
		if (File.Exists(podcastsJson) == false)
		{
			Log.Error($"Could not find {podcastsJson}");
			return 1;
		}
		
		var podcasts = new List<Podcast>();

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
		//var iaMappings = new List<LocalToInternetArchiveMapping>();


		// If you used RTPodcastArchiver you would put the same basePath here.
		//var iaMappingJsonFile = Path.Combine(inputPath, "ia_mapping.json");

		// Load it if it exists.
		/*
		if (File.Exists(iaMappingJsonFile))
		{
			using (var fileStream = File.OpenRead(iaMappingJsonFile))
			{
				var tempMappings = JsonSerializer.Deserialize<List<LocalToInternetArchiveMapping>>(fileStream);
				if (tempMappings != null)
				{
					iaMappings.AddRange(tempMappings);
				}
			}
		}
		else
		{
			// Otherwise create a dud one.
			var directoriesInLocalArchive = Directory.GetDirectories(archivePath);
			foreach (var directoryInLocalArchive in directoriesInLocalArchive)
			{
				iaMappings.Add(new LocalToInternetArchiveMapping()
				{
					LocalFolder = directoryInLocalArchive,
					LocalName = Path.GetFileName(directoryInLocalArchive),
				});
			}

			using (var fileStream = File.Create(iaMappingJsonFile))
			{
				JsonSerializer.Serialize(fileStream, iaMappings, new JsonSerializerOptions() { WriteIndented = true });
			}

			Log.Information($"You did not have an ia_mappings.json file before so one has been created at {iaMappingJsonFile}.");
			Log.Information($"Please update it appropriately by setting your internet archive identifier for that particular archive.");
			Log.Information($"This value would be something like rt-podcast-anma for this archive, https://archive.org/details/rt-podcast-anma");
			Log.Information($"Remember, these are unique to YOU.");
			Log.Information($"NOTE: Because this tool was built around my use case, the remote archives already exist. This tool will not create them from scratch.");
			return 1;
		}

		if (iaMappings.Count == 0)
		{
			Log.Error("Error: No mappings found.");
			return 1;
		}

		foreach (var iaMapping in iaMappings)
		{
			if (String.IsNullOrEmpty(iaMapping.IAIdentifier) == true)
			{
				Log.Error($"You did not set an ia_identifier for {iaMapping.LocalName}");
				return 1;
			}
		}
		*/


		// Used to update the README when new items are added.
		/*
		foreach (var podcast in podcasts)
		{
			Console.WriteLine($"| [{podcast.Name}](https://archive.org/details/{podcast.IAIdentifier}) | Uploading, Validating |");
		}
		*/

		
		
		// Uploads initial cover for creating new collections.
		/*
		foreach (var podcast in podcasts)
		{
			var localFolder = Path.Combine(archivePath, podcast.Name);
			Console.WriteLine("\n\n");
			Console.WriteLine($"Name: {podcast.Name}");
			Console.WriteLine($"Local folder: {localFolder}");
			Console.WriteLine($"IAIdentifier: {podcast.IAIdentifier}");

			var nameHasFirst = podcast.Name.Contains("first", StringComparison.OrdinalIgnoreCase);
			var folderHasFirst = localFolder.Contains("first", StringComparison.OrdinalIgnoreCase);
			var iaHasFirst = podcast.IAIdentifier.Contains("first", StringComparison.OrdinalIgnoreCase);

			if (nameHasFirst && folderHasFirst && iaHasFirst)
			{
				Console.WriteLine("Valid first");
			}
			else if (nameHasFirst == false && folderHasFirst == false && iaHasFirst == false)
			{
				Console.WriteLine("Valid non-first");
			}
			else
			{
				Console.WriteLine("Invalid first");
			}

			var pngCover = Path.Combine(localFolder, "cover.png");
			var jpgCover = Path.Combine(localFolder, "cover.jpg");

			var cover = string.Empty;

			if (File.Exists(pngCover))
			{
				cover = pngCover;
				Console.WriteLine("Cover: png");
			}
			else if (File.Exists(jpgCover))
			{
				cover = jpgCover;
				Console.WriteLine("Cover: jpg");
			}
			else
			{
				Console.WriteLine("Cover: Invalid cover");
			}

			try
			{
				
				Console.WriteLine($"{podcast.Name} - {podcast.IAIdentifier}");
				var processResultsNew = await ProcessEx.RunAsync($"ia", $"upload \"{podcast.IAIdentifier}\" \"{cover}\" --metadata=\"title:{podcast.Name}\" --metadata=\"creator:Rooster Teeth\"  --metadata=\"mediatype:audio\"   --metadata=\"collection:opensource_audio\"  --metadata=\"subject:Rooster Teeth; RoosterTeeth\" ");
				Console.WriteLine($"Exit code: {processResultsNew.ExitCode}");
				Debugger.Break();
				
			}
			catch (Exception err)
			{
				Console.WriteLine($"ERROR: {err.Message}");
				Debugger.Break();
			}
		}
		*/


		// This was used to create a list of old files to delete.
		/*
		var deleteList = Path.Combine(archivePath, "ia_to_delete.txt");
		using (var fileStream = File.Create(deleteList))
		{
			using (var streamWriter = new StreamWriter(fileStream))
			{
				foreach (var iaMapping in iaMappings)
				{
					try
					{
						var processResultsNew = await ProcessEx.RunAsync($"ia", $"list \"{iaMapping.IAIdentifier}\"");
						if (processResultsNew.ExitCode == 0)
						{
							var mp3ToDeletes = processResultsNew.StandardOutput.Where(x => x.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
							foreach (var mp3ToDelete in mp3ToDeletes)
							{
								if (mp3ToDelete.Contains("\""))
								{
									Debugger.Break();
								}
								streamWriter.WriteLine($"ia delete {iaMapping.IAIdentifier} \"{mp3ToDelete}\"");
							}
						}
						else
						{
							Console.WriteLine($"Exit code: {processResultsNew.ExitCode}");
							Debugger.Break();
						}

					}
					catch (Exception err)
					{
						Console.WriteLine($"ERROR: {err.Message}");
						Debugger.Break();
					}
				}
			}
		}
		*/

		// Used to try make some things thread safe.
		var lockObject = new Object();

		// We go through our mappings from top to bottom. To prioritise one over another move it to the top of the file.
		foreach (var podcast in podcasts)
		{
			if (podcast.IsEnabled == false)
			{
				continue;
			}
			
			var localPodcastFolder = Path.Combine(archivePath, podcast.Name);

			Log.Information($"Reading directory for {localPodcastFolder}");

			// Some data we read/create.
			var podcastRssFile = Path.Combine(localPodcastFolder, "podcast.rss");
			var uploadCsvFile = Path.Combine(localPodcastFolder, "upload.csv");
			var podcastXmlFile = Path.Combine(localPodcastFolder, "podcast.xml");
			var summaryJsonFile = Path.Combine(localPodcastFolder, "summary.json");

			// This is always created, so if it exists we delete it.
			if (File.Exists(uploadCsvFile))
			{
				File.Delete(uploadCsvFile);
			}

			// This is the summary file built in RTPodcastArchiver. It's used to map
			// a guid of a podcast episode we downloaded to a local file here.
			var fileSummaryDictionary = new Dictionary<string, FileSummary>();

			if (File.Exists(summaryJsonFile))
			{
				using (var fileStream = File.OpenRead(summaryJsonFile))
				{
					var tempFileSummaryList = JsonSerializer.Deserialize<List<FileSummary>>(fileStream);
					if (tempFileSummaryList == null)
					{
						Log.Error($"Error in {podcast.Name}: Could not deserialize summary.json.");
						return 1;
					}

					foreach (var fileSummary in tempFileSummaryList)
					{
						fileSummaryDictionary[$"{podcast.Name}_{fileSummary.Guid}"] = fileSummary;
					}
				}
			}
			else
			{
				Log.Error($"Error in {podcast.Name}: summary.json was not found.");
				//return 1;
				continue;
			}

			// We parse the podcast.xml, this is not uploaded however we create a podcast.rss for people to use.
			var xmlDoc = new XmlDocument();
			xmlDoc.Load(podcastXmlFile);

			// I don't know what xml namespaces are but we had to manually load them to be able to read some properties, eg atom:link
			var namespaceManager = new XmlNamespaceManager(new NameTable());
			var rssElement = xmlDoc.DocumentElement?.SelectSingleNode("/rss");
			if (rssElement == null)
			{
				Log.Information($"Error in {podcast.Name}: Could not find rss element.");
				continue;
			}

			// For each attribute that starts with xmlns: we load it
			if (rssElement.Attributes != null)
			{
				foreach (XmlAttribute attribute in rssElement.Attributes)
				{
					if (attribute.Name.StartsWith("xmlns:") == true)
					{
						var name = attribute.Name.Substring(6);
						var value = attribute.Value;
						namespaceManager.AddNamespace(name, value);
					}
				}
			}

			// We update the rss link from what it originally was, to where it will be on Internet Archive
			var atomLinkHrefAttribute = xmlDoc.DocumentElement?.SelectSingleNode("/rss/channel/atom:link", namespaceManager)?.Attributes?["href"];
			if (atomLinkHrefAttribute == null)
			{
				Log.Error($"Error in {podcast.Name}: Could not find the atom:link.");
				continue;
			}

			atomLinkHrefAttribute.Value = $"https://archive.org/download/{podcast.IAIdentifier}/podcast.rss";

			// Same thing, we update the podcast cover to the one on Internet Archive.
			var imageNode = xmlDoc.DocumentElement?.SelectSingleNode("/rss/channel/image/url");
			if (String.IsNullOrEmpty(imageNode?.InnerText) == true)
			{
				Log.Error($"Error in {podcast.Name}: Could not find the image url element.");
				continue;
			}

			var oldImageExtension = Path.GetExtension(new Uri(imageNode.InnerText).AbsolutePath);
			imageNode.InnerText = $"https://archive.org/download/{podcast.IAIdentifier}/cover{oldImageExtension}";

			// It also exists under a different tag.
			var itunesImageAttribute = xmlDoc.DocumentElement?.SelectSingleNode("/rss/channel/itunes:image", namespaceManager)?.Attributes?["href"];
			if (itunesImageAttribute == null)
			{
				Log.Error($"Error in {podcast.Name}: Could not find the itunes:image element.");
				continue;
			}

			itunesImageAttribute.Value = imageNode.InnerText;

			// Now we look at each episode.
			var itemNodes = xmlDoc.DocumentElement?.SelectNodes("/rss/channel/item");
			if (itemNodes == null)
			{
				Log.Error($"Error in {podcast.Name}: Could not find any episodes to parse.");
				continue;
			}

			// Go through each episode.
			foreach (XmlNode item in itemNodes)
			{
				// Guids are unique to each episode, we want to make sure we have it so we can reference the file.
				var guidNode = item.SelectSingleNode("guid");
				if (String.IsNullOrEmpty(guidNode?.InnerText) == true)
				{
					Log.Error($"Error in {podcast.Name}: Could not get episode guid.");
					continue;

					// At one point we were removing it, then replacing it, but we should keep it as is.
					//var guid = Guid.NewGuid();
					//guidNode.InnerXml = $"<![CDATA[{guid.ToString().ToLower()}]]>";
					//item.RemoveChild(guidNode);
				}

				var guid = guidNode?.InnerText;
				if (guid?.Length != 36)
				{
					// Convert the number to a Guid, as we did elsewhere.
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
						Log.Error($"Invalid guid, could not convert from int. ({guid})");
						continue;
					}
				}
				
				

				// This is the reference mentioned above.
				var fileSummary = fileSummaryDictionary?[$"{podcast.Name}_{guid}"];
				if (fileSummary == null)
				{
					Log.Error($"Error in {podcast.Name}: This item does not exist in our file summary map.");
					continue;
				}

				// If the actual length is -1 there may have been a problem with the download.
				if (fileSummary.ActualLength < 0)
				{
					Log.Error($"Error in {podcast.Name}: This file {fileSummary.LocalFilename} did not have any length, are you sure it exists?");
					continue;
				}

				// Now to get the enclosure node, this is what contains the download link for podcast apps.
				var enclosureNode = item.SelectSingleNode("enclosure");
				if (enclosureNode == null)
				{
					Log.Error($"Error in {podcast.Name}: Unable to get enclosure node.");
					continue;
				}

				var enclosureUrlAttribute = enclosureNode.Attributes?["url"];
				var enclosureLengthAttribute = enclosureNode.Attributes?["length"];

				if (enclosureUrlAttribute == null || enclosureLengthAttribute == null)
				{
					Log.Error($"Error in {podcast.Name}: enclosure does not have a url or length. This is not expected.");
					continue;
				}

				// Now we update these to be our new values.
				enclosureUrlAttribute.Value = $"https://archive.org/download/{podcast.IAIdentifier}/{Uri.EscapeDataString(Path.GetFileName(fileSummary.LocalFilename))}";
				enclosureLengthAttribute.Value = fileSummary.ActualLength.ToString();
			}

			// Write this new podcast out to disk. This is what is used as podcast.rss to use this podcast on your phone/computer.
			using (var fileStream = File.Create(podcastRssFile))
			{
				xmlDoc.Save(fileStream);
			}

			// We want to look at (almost) every file in this podcasts local directory and see if we need to upload it.
			var files = Directory.GetFiles(localPodcastFolder);

			// We want to use multiple threads to do this, but we don't want to throw 100 threads at it and make Internet Archive angry at your IP address
			var parallelOptions = new ParallelOptions()
			{
				MaxDegreeOfParallelism = 8, // cpu go brr
			};

			var filesToUpload = new List<UploadItem>();
			
			await Parallel.ForEachAsync(files, parallelOptions, async (file, token) =>
			{
				// What is this individual file name.
				var filename = Path.GetFileName(file);

				// This is where we expect the file to be.
				var remotePath = $"https://s3.us.archive.org/{podcast.IAIdentifier}/{filename}";

				// Skip some files that are not intended to be uploaded.
				if (file.EndsWith(".xml", StringComparison.InvariantCultureIgnoreCase) ||
				    file.EndsWith(".csv", StringComparison.InvariantCultureIgnoreCase) ||
				    file.EndsWith(".sh", StringComparison.InvariantCultureIgnoreCase) ||
				    file.EndsWith(".json", StringComparison.InvariantCultureIgnoreCase) ||
				    filename == ".DS_Store")
				{
					return;
				}

				// Now down to business.
				Log.Information($"Checking {filename}");


				// Internet archive actually does a 307 redirect to some S3 path, so first lets get the final path.
				var firstResponse = await httpClient.GetAsync(remotePath, HttpCompletionOption.ResponseHeadersRead);
				if (firstResponse.StatusCode != HttpStatusCode.RedirectKeepVerb)
				{
					Log.Information($"Error: Expected HttpStatusCode.RedirectKeepVerb, got HttpStatusCode.{firstResponse.StatusCode}");
					return;
				}

				// This is in the location header of where the file really is.
				var locationUrl = firstResponse.Headers.FirstOrDefault(x => x.Key.Equals("location", StringComparison.InvariantCultureIgnoreCase)).Value.FirstOrDefault();
				if (String.IsNullOrEmpty(locationUrl) == true)
				{
					Log.Information($"Error: Unable to get locationUrl");
					return;
				}

				// Now we get the headers of this file so we can check the ETag.
				// Also weird that the above won't 404 for a file that does not exist, but the below will.
				var locationResponse = await httpClient.GetAsync(locationUrl, HttpCompletionOption.ResponseHeadersRead);

				var shouldUpload = false;

				if (locationResponse.StatusCode == HttpStatusCode.NotFound)
				{
					// file does not exist, so we should upload it.
					shouldUpload = true;
				}
				else if (locationResponse.StatusCode == HttpStatusCode.OK)
				{
					// File exists but lets check the etag. Etag is a md5 hash/checksum of the file.
					var etag = locationResponse.Headers.FirstOrDefault(x => x.Key.Equals("etag", StringComparison.InvariantCultureIgnoreCase)).Value.FirstOrDefault();
					if (String.IsNullOrEmpty(etag) == true)
					{
						Log.Information("Error: File exists on InternetArchive but could not get ETag so not attempting to upload.");
						return;
					}

					// And for some reason they have double quotes, so lets remove them.
					etag = etag.Replace("\"", String.Empty);


					// Generate md5 hash of our local file to compare.
					var md5Hash = String.Empty;
					using (var fileStream = File.OpenRead(file))
					{
						using (var md5 = MD5.Create())
						{
							var hashBytes = md5.ComputeHash(fileStream);
							md5Hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
						}
					}

					// If they don't match we may have a partial upload, or the file changed, so we upload again.
					if (etag.Equals(md5Hash, StringComparison.InvariantCultureIgnoreCase) == false)
					{
						shouldUpload = true;
					}

					// Fun fact, even the podcast.rss file we generated above will have the same md5 hash if it has not changed.
				}

				// If we upload lets put it into our list of things to upload.
				if (shouldUpload)
				{
					// Lock as this can happen from multiple threads at the same time.
					lock (lockObject)
					{
						filesToUpload.Add(new UploadItem(file, remotePath));
					}
				}
			});

			// Ok, now we should know what we need to do, so lets output a summary for this podcast.
			Log.Information("Summary");
			Log.Information($"{podcast.Name} has {filesToUpload.Count} files to upload.");

			// So lets start the upload if there is anything to upload.
			if (filesToUpload.Count > 0)
			{
				Log.Information("Uploading");
				// Give this list a little sorty sort as our files are mostly sorted by date.
				filesToUpload.Sort((a, b) => a.LocalPath.CompareTo(b.LocalPath));
				foreach (var file in filesToUpload)
				{
					Log.Information($"{file.LocalPath} -> {file.RemotePath}");
				}

				// Originally we tired to upload with our HttpClient, but for whatever reason it did not like that.
				// Would regularly get socket exceptions about broken pipes.
				// I think the solution was to use multi-part uploads. But I figured I'd just use the ia CLI tool instead.
				/*
				//parallelOptions.MaxDegreeOfParallelism = 1;
				await Parallel.ForEachAsync(mapping.FilesToUpload, parallelOptions, async (fileToUpload, token) =>
				{
					using (var fileStream = File.OpenRead(fileToUpload.LocalPath))
					{
						using (var streamContent = new StreamContent(fileStream))
						{
							var size = fileStream.Length / 1000.0 / 1000.0;
							var filename = Path.GetFileName(fileToUpload.LocalPath);

							Log.Information($"Uploading {filename} ({size:0.00}MB)");

							try
							{
								var uploadResponse = await httpClient.PutAsync(fileToUpload.RemotePath, streamContent);
								if (uploadResponse.StatusCode != HttpStatusCode.OK)
								{
									Log.Information($"Error with {filename}: StatusCode {uploadResponse.StatusCode}");
								}
							}
							catch (Exception err)
							{
								var internalException = err.InnerException?.Message ?? String.Empty;
								Log.Information($"Error with {filename}: {err.Message}, {internalException}");
							}
						}
					}
				});
				*/

				// So what I did is have this program create a upload.csv and upload.sh where the upload.sh would call
				// ia upload --spreadsheet=upload.csv --checksum
				// on and upload via the ia tool. Instead we will upload manually with the ia tool.
				// It doesn't work on all platforms, also the ia tool can be janky for uploading from spreadsheet.
				/*
				using (var fileStream = File.Create(uploadCsvFile))
				{
					using (var streamWriter = new StreamWriter(fileStream))
					{
						streamWriter.WriteLine("identifier,file");

						foreach (var fileToUpload in iaMapping.FilesToUpload)
						{
							streamWriter.WriteLine($"{iaMapping.IAIdentifier},\"{fileToUpload.LocalPath}\"");
						}
					}
				}

				var uploadShFile = Path.Combine(iaMapping.LocalFolder, "upload.sh");

				using (var fileStream = File.Create(uploadShFile))
				{
					using (var streamWriter = new StreamWriter(fileStream))
					{
						streamWriter.WriteLine("#!/bin/bash");
						streamWriter.WriteLine("set -e");
						streamWriter.WriteLine("ia upload --spreadsheet=upload.csv --checksum");
					}
				}
				*/

				
				
				// Third times a charm?
				// Using ia upload ClI, but multiple times per podcast.
				await Parallel.ForEachAsync(filesToUpload, parallelOptions, async (fileToUpload, token) =>
				{
					Log.Information($"Uploading: {fileToUpload.LocalPath}");

					try
					{
						Log.Information($"Attempting CLI upload: ia upload {podcast.IAIdentifier} \"{fileToUpload.LocalPath}\"");
	
						var processResults = await ProcessEx.RunAsync($"ia", $"upload {podcast.IAIdentifier} \"{fileToUpload.LocalPath}\"");
						if (processResults.ExitCode != 0)
						{
							var errorString = String.Join("\n", processResults.StandardOutput);
							throw new Exception(errorString);
						}

						Log.Information($"Success: {fileToUpload.LocalPath}");
					}
					catch (Exception err)
					{
						Log.Error(err, $"\nCould not upload {fileToUpload.LocalPath}\n{err.Message}\n");
					}
				});
			}
			else
			{
				Log.Information("No files to upload.");
				return 0;
			}
		}

		Log.Information("Done");
		return 0;
	}
}