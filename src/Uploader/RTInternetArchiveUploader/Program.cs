using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices.JavaScript;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using RTInternetArchiveUploader;
using RunProcessAsTask;

// First make sure ia is installed.
try
{
	var processResults = await ProcessEx.RunAsync("ia", "--version");
	if (processResults.ExitCode != 0)
	{
		throw new Exception("Could not get ia version.");
	}
	
	// Could make sure we have the version number, but this should be fine.
	Console.WriteLine($"Using ia v{processResults.StandardOutput[0]}");
}
catch (Exception err)
{
	Console.WriteLine("Error: ia (internet archive command-line tool) not found. Are you sure it is installed?");
	Console.WriteLine(err.Message);
	return;
}

// We do web requests with accessKey and secretKey for the IA-s3 like API.
// If you have not configured ia please see https://archive.org/developers/internetarchive/quickstart.html
// We will attempt to load them below, but feel free to skip that by specifying them here.
var accessKey = String.Empty;
var secretKey = String.Empty;

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
			Console.WriteLine("Could not get home path.");
			return;
		}
		
		var tempIaConfigFile = Path.Combine(homePath, ".config", "internetarchive", "ia.ini");
		if (File.Exists(tempIaConfigFile))
		{
			iaConfigFile = tempIaConfigFile;
		}
	}

	if (String.IsNullOrEmpty(iaConfigFile) == true || File.Exists(iaConfigFile) == false)
	{
		Console.WriteLine("Unable to load your ia.ini file. You can avoid this if you hard code your access_key and secretKey in the source code itself.");
		return;
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
		Console.WriteLine($"Unable to load your access or secret from {iaConfigFile}");
		Console.WriteLine("We use a regex that is specifically looking for");
		Console.WriteLine("[s3]");
		Console.WriteLine("access = abc");
		Console.WriteLine("secret = 123");
		Console.WriteLine("If you are using a non standard config file you can try setting accessKey and secretKey in the source itself.");
	}
}

if (String.IsNullOrEmpty(accessKey) == true || String.IsNullOrEmpty(secretKey) == true)
{
	Console.WriteLine("Can't continue without an accessKey or secretKey");
	return;
}

// Some nonsense when I was trying to fix timeouts.
/*
var socketsHttpHandler = new SocketsHttpHandler();
socketsHttpHandler.ConnectTimeout = TimeSpan.FromMinutes(60);
socketsHttpHandler.Expect100ContinueTimeout = TimeSpan.FromMinutes(60);
socketsHttpHandler.ResponseDrainTimeout = TimeSpan.FromMinutes(60);
socketsHttpHandler.KeepAlivePingTimeout = TimeSpan.FromMinutes(60);
socketsHttpHandler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(60);
*/


// Can't use AllowAutoRedirect from HttpClientHandler as it does not redirect
// from https to http which is what IA seems to do.
var httpClient = new HttpClient();
httpClient.Timeout = TimeSpan.FromMinutes(15);

// This is where we used the accessKey and secretKey from above.
httpClient.DefaultRequestHeaders.Add("authorization", $"LOW {accessKey}:{secretKey}");

// This is what we use to map local files to your remote Internet Archive archive.
var iaMappings = new List<LocalToInternetArchiveMapping>();


// If you used RTPodcastArchiver you would put the same basePath here.
var basePath = "/Volumes/Storage/RT Podcast Archive";
var iaMappingJsonFile = Path.Combine(basePath, "ia_mapping.json");

// Load it if it exists.
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
	var directoriesInLocalArchive = Directory.GetDirectories(basePath);
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
	Console.WriteLine($"You did not have an ia_mappings.json file before so one has been created at {iaMappingJsonFile}.");
	Console.WriteLine($"Please update it appropriately by setting your internet archive identifier for that particular archive.");
	Console.WriteLine($"This value would be something like rt-podcast-anma for this archive, https://archive.org/details/rt-podcast-anma");
	Console.WriteLine($"Remember, these are unique to YOU.");
	Console.WriteLine($"NOTE: Because this tool was built around my use case, the remote archives already exist. This tool will not create them from scratch.");
	return;
}

if (iaMappings.Count == 0)
{
	Console.WriteLine("Error: No mappings found.");
	return;
}

foreach (var iaMapping in iaMappings)
{
	if (String.IsNullOrEmpty(iaMapping.IAIdentifier) == true)
	{
		Console.WriteLine($"ERROR: You did not set an ia_identifier for {iaMapping.LocalName}");
		return;
	}
}

// Used to try make some things thread safe.
var lockObject = new Object();

// We go through our mappings from top to bottom. To prioritise one over another move it to the top of the file.
foreach (var iaMapping in iaMappings)
{
	Console.WriteLine($"Reading directory for {iaMapping.LocalName}");

	// Some data we read/create.
	var podcastRssFile = Path.Combine(iaMapping.LocalFolder, "podcast.rss");
	var uploadCsvFile = Path.Combine(iaMapping.LocalFolder, "upload.csv");
	var podcastXmlFile = Path.Combine(iaMapping.LocalFolder, "podcast.xml");
	var summaryJsonFile = Path.Combine(iaMapping.LocalFolder, "summary.json");

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
				Console.WriteLine($"Error in {iaMapping.LocalName}: Could not deserialize summary.json.");
				return;
			}

			foreach (var fileSummary in tempFileSummaryList)
			{
				fileSummaryDictionary[fileSummary.Guid] = fileSummary;
			}
		}
	}
	else
	{
		Console.WriteLine($"Error in {iaMapping.LocalName}: summary.json was not found.");
		return;
	}

	// We parse the podcast.xml, this is not uploaded however we create a podcast.rss for people to use.
	var xmlDoc = new XmlDocument();
	xmlDoc.Load(podcastXmlFile);

	// I don't know what xml namespaces are but we had to manually load them to be able to read some properties, eg atom:link
	var namespaceManager = new XmlNamespaceManager(new NameTable());
	var rssElement = xmlDoc.DocumentElement?.SelectSingleNode("/rss");
	if (rssElement == null)
	{
		Console.WriteLine($"Error in {iaMapping.LocalName}: Could not find rss element.");
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
		Console.WriteLine($"Error in {iaMapping.LocalName}: Could not find the atom:link.");
		continue;
	}
	atomLinkHrefAttribute.Value = $"https://archive.org/download/{iaMapping.IAIdentifier}/podcast.rss";

	// Same thing, we update the podcast cover to the one on Internet Archive.
	var imageNode = xmlDoc.DocumentElement?.SelectSingleNode("/rss/channel/image/url");
	if (String.IsNullOrEmpty(imageNode?.InnerText) == true)
	{
		Console.WriteLine($"Error in {iaMapping.LocalName}: Could not find the image url element.");
		continue;
	}
	var oldImageExtension = Path.GetExtension(new Uri(imageNode.InnerText).AbsolutePath);
	imageNode.InnerText = $"https://archive.org/download/{iaMapping.IAIdentifier}/cover{oldImageExtension}";

	// It also exists under a different tag.
	var itunesImageAttribute = xmlDoc.DocumentElement?.SelectSingleNode("/rss/channel/itunes:image", namespaceManager)?.Attributes?["href"];
	if (itunesImageAttribute == null)
	{
		Console.WriteLine($"Error in {iaMapping.LocalName}: Could not find the itunes:image element.");
		continue;
	}
	itunesImageAttribute.Value = imageNode.InnerText;
	
	// Now we look at each episode.
	var itemNodes = xmlDoc.DocumentElement?.SelectNodes("/rss/channel/item");
	if (itemNodes == null)
	{
		Console.WriteLine($"Error in {iaMapping.LocalName}: Could not find any episodes to parse.");
		continue;
	}

	// Go through each episode.
	foreach (XmlNode item in itemNodes)
	{
		// Guids are unique to each episode, we want to make sure we have it so we can reference the file.
		var guidNode = item.SelectSingleNode("guid");
		if (String.IsNullOrEmpty(guidNode?.InnerText) == true)
		{
			Console.WriteLine($"Error in {iaMapping.LocalName}: Could not get episode guid.");
			continue;
			
			// At one point we were removing it, then replacing it, but we should keep it as is.
			//var guid = Guid.NewGuid();
			//guidNode.InnerXml = $"<![CDATA[{guid.ToString().ToLower()}]]>";
			//item.RemoveChild(guidNode);
		}
		
		// This is the reference mentioned above.
		var fileSummary = fileSummaryDictionary[guidNode.InnerText];
		if (fileSummary == null)
		{
			Console.WriteLine($"Error in {iaMapping.LocalName}: This item does not exist in our file summary map.");
			continue;
		}

		// If the actual length is -1 there may have been a problem with the download.
		if (fileSummary.ActualLength < 0)
		{
			Console.WriteLine($"Error in {iaMapping.LocalName}: This file {fileSummary.LocalFilename} did not have any length, are you sure it exists?");
			continue;
		}
		
		// Now to get the enclosure node, this is what contains the download link for podcast apps.
		var enclosureNode = item.SelectSingleNode("enclosure");
		if (enclosureNode == null)
		{
			Console.WriteLine($"Error in {iaMapping.LocalName}: Unable to get enclosure node.");
			continue;
		}

		var enclosureUrlAttribute = enclosureNode.Attributes?["url"];
		var enclosureLengthAttribute = enclosureNode.Attributes?["length"];

		if (enclosureUrlAttribute == null || enclosureLengthAttribute == null)
		{
			Console.WriteLine($"Error in {iaMapping.LocalName}: enclosure does not have a url or length. This is not expected.");
			continue;
		}
		
		// Now we update these to be our new values.
		enclosureUrlAttribute.Value = $"https://archive.org/download/{iaMapping.IAIdentifier}/{Uri.EscapeDataString(Path.GetFileName(fileSummary.LocalFilename))}";
		enclosureLengthAttribute.Value = fileSummary.ActualLength.ToString();
	}

	// Write this new podcast out to disk. This is what is used as podcast.rss to use this podcast on your phone/computer.
	using (var fileStream = File.Create(podcastRssFile))
	{
		xmlDoc.Save(fileStream);
	}
	
	// We want to look at (almost) every file in this podcasts local directory and see if we need to upload it.
	var files = Directory.GetFiles(iaMapping.LocalFolder);
	
	// We want to use multiple threads to do this, but we don't want to throw 100 threads at it and make Internet Archive angry at your IP address
	var parallelOptions = new ParallelOptions()
	{
		MaxDegreeOfParallelism = 8, // cpu go brr
	};
	await Parallel.ForEachAsync(files, parallelOptions, async (file, token) =>
	{
		// What is this individual file name.
		var filename = Path.GetFileName(file);
		
		// This is where we expect the file to be.
		var remotePath = $"https://s3.us.archive.org/{iaMapping.IAIdentifier}/{filename}";
		
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
		Console.WriteLine($"Checking {filename}");
		
		
		// Internet archive actually does a 307 redirect to some S3 path, so first lets get the final path.
		var firstResponse = await httpClient.GetAsync(remotePath, HttpCompletionOption.ResponseHeadersRead);
		if (firstResponse.StatusCode != HttpStatusCode.RedirectKeepVerb)
		{
			Console.WriteLine($"Error: Expected HttpStatusCode.RedirectKeepVerb, got HttpStatusCode.{firstResponse.StatusCode}");
			return;
		}
		
		// This is in the location header of where the file really is.
		var locationUrl = firstResponse.Headers.FirstOrDefault(x => x.Key.Equals("location", StringComparison.InvariantCultureIgnoreCase)).Value.FirstOrDefault();
		if (String.IsNullOrEmpty(locationUrl) == true)
		{
			Console.WriteLine($"Error: Unable to get locationUrl");
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
				Console.WriteLine("Error: File exists on InternetArchive but could not get ETag so not attempting to upload.");
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
				iaMapping.FilesToUpload.Add(new UploadItem(file, remotePath));
			}
		}
	});
	
	// Ok, now we should know what we need to do, so lets output a summary for this podcast.
	Console.WriteLine("Summary");
	Console.WriteLine($"{iaMapping.LocalName} has {iaMapping.FilesToUpload.Count} files to upload.");
	
	// So lets start the upload if there is anything to upload.
	if (iaMapping.FilesToUpload.Count > 0)
	{
		Console.WriteLine("Uploading");
		// Give this list a little sorty sort as our files are mostly sorted by date.
		iaMapping.FilesToUpload.Sort((a, b) => a.LocalPath.CompareTo(b.LocalPath) );
		
		
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

					Console.WriteLine($"Uploading {filename} ({size:0.00}MB)");

					try
					{
						var uploadResponse = await httpClient.PutAsync(fileToUpload.RemotePath, streamContent);
						if (uploadResponse.StatusCode != HttpStatusCode.OK)
						{
							Console.WriteLine($"Error with {filename}: StatusCode {uploadResponse.StatusCode}");
						}
					}
					catch (Exception err)
					{
						var internalException = err.InnerException?.Message ?? String.Empty;
						Console.WriteLine($"Error with {filename}: {err.Message}, {internalException}");
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
		await Parallel.ForEachAsync(iaMapping.FilesToUpload, parallelOptions, async (fileToUpload, token) =>
		{
			Console.WriteLine($"Uploading: {fileToUpload.LocalPath}");
			
			try
			{
				// TODO: Some files have double quotes in the name, is this a problem with this uploader?
				var processResults = await ProcessEx.RunAsync($"ia", $"upload {iaMapping.IAIdentifier} \"{fileToUpload.LocalPath}\"");
				if (processResults.ExitCode != 0)
				{
					var errorString = String.Join("\n", processResults.StandardOutput);
					throw new Exception(errorString);
				}
				
				Console.WriteLine($"Success: {fileToUpload.LocalPath}");
			}
			catch (Exception err)
			{
				Console.WriteLine($"\nERROR: Could not upload {fileToUpload.LocalPath}\n{err.Message}\n");
			}
		});
	}
}

Console.WriteLine("Done");
