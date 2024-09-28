using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace SteamWorkshopDownloader
{
    internal class Program
    {
        private static readonly HttpClient client = new HttpClient();

        static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand("Steam Workshop Downloader");

            var steamCMDPath = new Option<FileInfo?>(name: "--steamcmdpath", description: "The SteamCMD executable.");
            steamCMDPath.IsRequired = true;
            rootCommand.AddOption(steamCMDPath);

            var gameId = new Option<ulong>(name: "--gameid", description: "The Steam Game ID.");
            gameId.IsRequired = true;

            var output = new Option<DirectoryInfo?>(name: "--output", description: "The output directory.");

            var collection = new Option<string>(name: "--collection", description: "The Steam Collection.");
            steamCMDPath.IsRequired = true;

            var downloadCollectionCommand = new Command("downloadcollection", "Downloads a Steam Collection.")
            {
                steamCMDPath,
            };
            rootCommand.AddCommand(downloadCollectionCommand);
            downloadCollectionCommand.AddOption(collection);
            downloadCollectionCommand.AddOption(gameId);
            downloadCollectionCommand.AddOption(output);

            downloadCollectionCommand.SetHandler(async (FileInfo? steamcmd, ulong gameId, string collection, DirectoryInfo? outputDirectory) =>
            {
                await DownloadCollection(steamcmd, gameId, collection, outputDirectory ?? new DirectoryInfo(Directory.GetCurrentDirectory()));
            }, steamCMDPath, gameId, collection, output);

            return await rootCommand.InvokeAsync(args);
        }

        public static async Task DownloadCollection(FileInfo steamcmd, ulong gameId, string collection, DirectoryInfo outputDirectory)
        {
            if (collection.StartsWith("https://"))
            {
                // Grab the ID from the URL after ?id=
                collection = collection.Split("?id=")[1];
            }

            var values = new Dictionary<string, string>
            {
                { "collectioncount", "1" },
                { "publishedfileids[0]", collection }
            };

            var content = new FormUrlEncodedContent(values);
            var response = await client.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/", content);
            var responseString = await response.Content.ReadAsStringAsync();

            JObject collectionJson = JObject.Parse(responseString);

            IList<JToken> results = collectionJson["response"]["collectiondetails"].Children().ToList();

            List<ulong> itemIds = new List<ulong>();

            foreach (JToken result in results)
            {
                IList<JToken> items = result["children"].Children().ToList();

                foreach (JToken item in items)
                {
                    string id = item["publishedfileid"].ToString();

                    itemIds.Add(ulong.Parse(id));
                }
            }

            for (int i = 0; i < itemIds.Count; i++)
            {
                try 
                {
                    await DownloadItem(gameId, itemIds[i], steamcmd, new DirectoryInfo(Path.Combine(outputDirectory.FullName, itemIds[i].ToString())));
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception.Message);
                }
            }
        }

        public static async Task DownloadItem(ulong gameId, ulong itemId, FileInfo steamcmd, DirectoryInfo directory)
        {
            string currentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = steamcmd.FullName,
                Arguments = $"+force_install_dir {currentDirectory} +login anonymous +workshop_download_item {gameId} {itemId} +quit"
            });

            bool downloadFailed = false;

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data == null) { return; }
                
                if (e.Data.Contains("ERROR!"))
                {
                    downloadFailed = true;
                }
            };


            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                downloadFailed = true;
            }

            if (downloadFailed)
            {
                throw new Exception($"Failed to download item {itemId}.");
            }

            // copy the folder to the directory
            string path = Path.Combine(currentDirectory, "steamapps", "workshop", "content", gameId.ToString(), itemId.ToString());

            if (Directory.Exists(directory.FullName))
            {
                Directory.Delete(directory.FullName, true);
            }

            Directory.Move(path, Path.Combine(directory.FullName));
        }
    }
}
