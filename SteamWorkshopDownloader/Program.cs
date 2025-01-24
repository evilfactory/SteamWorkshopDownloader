using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Xml;
using Luatrauma.AutoUpdater;
using Newtonsoft.Json.Linq;

namespace SteamWorkshopDownloader
{
    internal class Program
    {
        private static readonly HttpClient client = new HttpClient();

        static void CopyDirectory(string sourceDir, string destinationDir, bool recursive)
        {
            var dir = new DirectoryInfo(sourceDir);

            if (!dir.Exists)
                throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

            DirectoryInfo[] dirs = dir.GetDirectories();

            Directory.CreateDirectory(destinationDir);

            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath);
            }

            if (recursive)
            {
                foreach (DirectoryInfo subDir in dirs)
                {
                    string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                    CopyDirectory(subDir.FullName, newDestinationDir, true);
                }
            }
        }

        static async Task<int> Main(string[] args)
        {
            var rootCommand = new RootCommand("Steam Workshop Downloader");

            var steamCMDPath = new Option<FileInfo?>(name: "--steamcmdpath", description: "The SteamCMD executable.");
            steamCMDPath.IsRequired = true;

            var steamappsFolder = new Option<DirectoryInfo?>(name: "--steamappsfolder", description: "The SteamApps folder.");
            steamappsFolder.IsRequired = true;

            var gameId = new Option<string>(name: "--gameid", description: "The Steam Game ID.");
            gameId.IsRequired = true;

            var output = new Option<DirectoryInfo?>(name: "--output", description: "The output directory.");

            var collection = new Option<string>(name: "--collection", description: "The Steam Collection.");
            steamCMDPath.IsRequired = true;

            var downloadCollectionCommand = new Command("downloadcollection", "Downloads a Steam Collection.")
            {
                steamCMDPath,
                steamappsFolder,
                collection,
                gameId,
                output
            };

            rootCommand.AddCommand(downloadCollectionCommand);

            var modFolder = new Option<DirectoryInfo?>(name: "--modfolder", description: "The mod folder directory.");
            modFolder.IsRequired = true;
            var configPlayer = new Option<FileInfo?>(name: "--configplayerfile", description: "The config player file.");
            configPlayer.IsRequired = true;

            var btSetConfigPlayer = new Command("btsetconfigplayer", "Goes through every single mod in the specified folder and adds them to the config_player.xml.")
            {
                modFolder,
                configPlayer
            };

            rootCommand.AddCommand(btSetConfigPlayer);

            var btSetConfigFromCollection = new Command("btsetconfigfromcollection", "Retrieves the collection and adds only the mods present in the collection to config_player_xml, in order.")
            {
                collection,
                configPlayer
            };

            rootCommand.AddCommand(btSetConfigFromCollection);

            downloadCollectionCommand.SetHandler(async (FileInfo? steamcmd, DirectoryInfo? steamappsFolder, string gameId, string collection, DirectoryInfo? outputDirectory) =>
            {
                await DownloadCollection(steamcmd, steamappsFolder, gameId, collection, outputDirectory ?? new DirectoryInfo(Directory.GetCurrentDirectory()));
            }, steamCMDPath, steamappsFolder, gameId, collection, output);

            btSetConfigPlayer.SetHandler(async (DirectoryInfo? modFolder, FileInfo? configPlayer) =>
            {
                if (!modFolder.Exists || !configPlayer.Exists) { return; }

                await BTSetConfigPlayer(modFolder, configPlayer);
            }, modFolder, configPlayer);

            btSetConfigFromCollection.SetHandler(async (string collection, FileInfo? configPlayer) =>
            {
                if (!configPlayer.Exists) { return; }
                await BTSetConfigFromCollection(collection, configPlayer);
            }, collection, configPlayer);

            return await rootCommand.InvokeAsync(args);
        }

        public static async Task BTSetConfigFromCollection(string collection, FileInfo configPlayer)
        {
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

            List<string> itemIds = new List<string>();

            foreach (JToken result in results)
            {
                IList<JToken> items = result["children"].Children().ToList();

                foreach (JToken item in items)
                {
                    string id = item["publishedfileid"].ToString();

                    itemIds.Add(id);
                }
            }

            // parse the configPlayer XML 
            XmlDocument configPlayerDoc = new XmlDocument();
            configPlayerDoc.Load(configPlayer.FullName);

            XmlNode node = configPlayerDoc.DocumentElement.SelectSingleNode("contentpackages/regularpackages");

            // clear the regular packages
            node.RemoveAll();

            // add the new packages
            foreach (string itemId in itemIds)
            {
                XmlElement package = configPlayerDoc.CreateElement("package");
                package.SetAttribute("path", $"LocalMods/{itemId}/filelist.xml");

                node.AppendChild(package);

                Logger.Log($"Added {itemId} to the {configPlayer}");
            }

            // save to the file
            configPlayerDoc.Save(configPlayer.FullName);
        }

        public static async Task BTSetConfigPlayer(DirectoryInfo modFolder, FileInfo configPlayer)
        {
            List<string> packagePaths = new List<string>();

            // Scan the modFolder for all the mods
            DirectoryInfo[] modFolders = modFolder.GetDirectories();

            for (int i = 0; i < modFolders.Length; i++)
            {
                var folder = modFolders[i];

                FileInfo filelist = new FileInfo(Path.Combine(folder.FullName, "filelist.xml"));

                if (!filelist.Exists)
                {
                    continue;
                }

                packagePaths.Add($"LocalMods/{folder.Name}/filelist.xml");
            }


            // parse the configPlayer XML 
            XmlDocument configPlayerDoc = new XmlDocument();
            configPlayerDoc.Load(configPlayer.FullName);

            XmlNode node = configPlayerDoc.DocumentElement.SelectSingleNode("contentpackages/regularpackages");

            // clear the regular packages
            node.RemoveAll();

            // add the new packages
            foreach (string packagePath in packagePaths)
            {
                XmlElement package = configPlayerDoc.CreateElement("package");
                package.SetAttribute("path", packagePath);

                node.AppendChild(package);

                Logger.Log($"Added {packagePath} to the {configPlayer}");
            }

            // save to the file
            configPlayerDoc.Save(configPlayer.FullName);
        }

        public static async Task DownloadCollection(FileInfo steamcmd, DirectoryInfo steamappsFolder, string gameId, string collection, DirectoryInfo outputDirectory)
        {
            if (collection.StartsWith("https://"))
            {
                // Grab the ID from the URL after ?id=
                collection = collection.Split("?id=")[1];
            }

            Logger.Log($"Downloading collection {collection}.", ConsoleColor.Cyan);

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

            List<string> itemIds = new List<string>();

            foreach (JToken result in results)
            {
                IList<JToken> items = result["children"].Children().ToList();

                foreach (JToken item in items)
                {
                    string id = item["publishedfileid"].ToString();

                    itemIds.Add(id);
                }
            }

            async Task<bool> tryDownload(string itemId)
            {
                try
                {
                    await DownloadItem(gameId, itemId, steamcmd, steamappsFolder, new DirectoryInfo(Path.Combine(outputDirectory.FullName, itemId.ToString())));
                    return true;
                }
                catch (Exception exception)
                {
                    Logger.Log($"Failed to download {itemId} with exception {exception.Message}.", ConsoleColor.Red);
                    return false;
                }
            }

            for (int i = 0; i < itemIds.Count; i++)
            {
                Logger.Log("");
                Logger.Log($"Starting to download {itemIds[i]}.", ConsoleColor.Cyan);

                for (int j = 0; j < 3; j++)
                {
                    if (await tryDownload(itemIds[i]))
                    {
                        Logger.Log("");
                        break;
                    }
                    else
                    {
                        Logger.Log($"Failed to download {itemIds[i]},\ntrying again...", ConsoleColor.Red);
                        await tryDownload(itemIds[i]);
                    }
                }
            }
        }

        public static async Task DownloadItem(string gameId, string itemId, FileInfo steamcmd, DirectoryInfo steamappsFolder, DirectoryInfo directory)
        {
            string currentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = steamcmd.FullName,
                Arguments = $"+login anonymous +workshop_download_item {gameId} {itemId} validate +quit",
                UseShellExecute = false,
                RedirectStandardOutput = true
            });

            bool downloadFailed = false;

            while (!process.StandardOutput.EndOfStream)
            {
                string line = process.StandardOutput.ReadLine();
                Logger.Log(line);

                if (line.Contains("ERROR!"))
                {
                    downloadFailed = true;
                }
            }

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
            string path = Path.Combine(steamappsFolder.FullName, "steamapps", "workshop", "content", gameId.ToString(), itemId.ToString());

            if (Directory.Exists(directory.FullName))
            {
                Directory.Delete(directory.FullName, true);
            }

            CopyDirectory(path, Path.Combine(directory.FullName), true);
        }
    }
}
