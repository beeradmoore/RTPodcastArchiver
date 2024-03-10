# RTPodcastArhciver
This content is already used and archived. This repo is mostly for educational purposes.


First, the `Program.cs` source needs to be modified before you can use it.

1. Update `basePath` to where you want your archive to go.
2. By default this can't access the podcasts without your user tokens.
3. Go to https://roosterteeth.supportingcast.fm/subscription/type/podcast to get your rss links
4. Add your rss links into `Program.cs`, there are samples where you should add this.
5. Important!, don't commit those changes to any public repository as they are URLs specific to you.
6. This will generate a `podcasts.json` in the path of your `basePath` above. 
7. If you re-run the application it will download any new episodes using `podcasts.json` as a list of target podcasts.
Example: 
```csharp
podcasts.Add(new Podcast()
{
	Name = "F**kface",
	Url = "https://roosterteeth.supportingcast.fm/content/eyABC.....123.rss",
});
```

For any other information check the commets in `Program.cs`.

For any other questions or comments feel free to file an [issue](https://github.com/beeradmoore/RTPodcastArchiver/issues) on this repository.