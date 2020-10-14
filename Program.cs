using CommandLine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace egsmv
{
    class Options
    {
        [Option('n', "name", Required = true, HelpText = "Specify which game to move")]
        public string PartialDisplayName { get; set; }

        [Option('v', "verbose", Required = false, HelpText = "Set output to verbose")]
        public bool Verbose { get; set; } = false;

        [Option('d', "destination", Required = true, HelpText = "Where to move the game")]
        public string Destination { get; set; }

        [Option('x', "skipdelete", Required = false, HelpText = "Don't delete the original files")]
        public bool SkipDelete { get; set; } = false;
    }

    class AppData
    {
        public string DisplayName { get; set; }
        public string AppName { get; set; }
        public string MainGameAppName { get; set; }
        public string ManifestPath { get; set; }

        public string InstallLocation { get; set; }
        public string MandatoryAppFolderName { get; set; }

        public bool IsMainGame => AppName == MainGameAppName;
    }

    enum FindGameResultType
    {
        Found,
        NotFound,
        MultipleFound,
    }

    class FindGameResult
    {
        public FindGameResultType Tag;
        public IEnumerable<AppData> MatchingAppData;

        public FindGameResult WithFound(Action<AppData> action)
        {
            if (Tag == FindGameResultType.Found)
            {
                action(MatchingAppData.First());
            }
            return this;
        }

        public FindGameResult WithMultipleFound(Action<IEnumerable<AppData>> action)
        {
            if (Tag == FindGameResultType.MultipleFound)
            {
                action(MatchingAppData);
            }
            return this;
        }

        public FindGameResult WithNotFound(Action action)
        {
            if (Tag == FindGameResultType.NotFound)
            {
                action();
            }
            return this;
        }

        public FindGameResult WithAny(Action<IEnumerable<AppData>> action)
        {
            if (Tag != FindGameResultType.NotFound)
            {
                action(MatchingAppData);
            }
            return this;
        }
    }

    class Program
    {
        static void Log(Options o, string message)
        {
            Console.WriteLine(message);
        }

        static void LogVerbose(Options o, string message)
        {
            if (o.Verbose)
                Console.WriteLine(message);
        }
        static void LogError(Options _, string message)
        {
            Console.WriteLine(message);
        }
        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args).WithParsed<Options>(options => {
                var appDatabase = ConstructAppDatabase(options);
                FindMatchingGame(appDatabase, options.PartialDisplayName)
                .WithFound(mainGameAppData =>
                {
                    FindGameAndAllDLC(appDatabase, mainGameAppData.AppName)
                    .WithAny(gameAndDlcAppData =>
                    {
                        foreach(var matchingAppData in gameAndDlcAppData)
                        {
                            Log(options, $"{(matchingAppData.IsMainGame ? "Game" : " DLC")}: {matchingAppData.DisplayName}");
                            UpdateManifestLocations(options, mainGameAppData, matchingAppData);
                        }

                        MoveDirectory(options, mainGameAppData, options.Destination);
                    });
                })
                .WithMultipleFound(matchingAppDatas =>
                {
                    LogError(options, "Found multiple matching games, please be more specific:");
                    foreach(var appData in matchingAppDatas)
                    {
                        LogError(options, $"\t{appData.DisplayName}");
                    }
                })
                .WithNotFound(() =>
                {
                    LogError(options, $"Couldn't find any games matching '{options.PartialDisplayName}'");
                });
            });
        }

        static IEnumerable<AppData> ConstructAppDatabase(Options options)
        {
            List<AppData> appDatabase = new List<AppData>();
            string manifestFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
            LogVerbose(options, $"Constructing database of app manifests ({manifestFolder})");
            foreach (var manifestFilePath in Directory.GetFiles(manifestFolder, "*.item"))
            {
                var appData = LoadAppDataFromManifest(options, manifestFilePath);
                appDatabase.Add(appData);
            }
            return appDatabase;
        }

        static AppData LoadAppDataFromManifest(Options options, string manifestFilePath)
        {
            using (StreamReader manifestTextReader = File.OpenText(manifestFilePath))
            using (JsonTextReader manifestJsonReader = new JsonTextReader(manifestTextReader))
            {
                JObject jsonManifest = (JObject)JToken.ReadFrom(manifestJsonReader);
                string appName = Convert.ToString(jsonManifest["AppName"]);
                string displayName = Convert.ToString(jsonManifest["DisplayName"]);
                string mainGameAppName = Convert.ToString(jsonManifest["MainGameAppName"]);

                string installLocation = Convert.ToString(jsonManifest["InstallLocation"]);
                string mandatoryAppFolderName = Convert.ToString(jsonManifest["MandatoryAppFolderName"]);

                LogVerbose(options, $"Found manifest for {displayName} (AppName={appName}, MainGameAppName={mainGameAppName})");

                return new AppData() {
                    AppName = appName,
                    DisplayName = displayName,
                    MainGameAppName = mainGameAppName,
                    ManifestPath = manifestFilePath,
                    InstallLocation = installLocation,
                    MandatoryAppFolderName = mandatoryAppFolderName,
                };
            }
        }

        static void UpdateManifestLocations(Options options, AppData mainGameAppData, AppData appData)
        {
            LogVerbose(options, $"Updating manifest {appData.DisplayName} ({appData.ManifestPath})");
            var destinationPath = GetDestinationPath(options.Destination, mainGameAppData);
            var allText = File.ReadAllText(appData.ManifestPath);
            using (StringReader manifestStringReader = new StringReader(allText))
            using (JsonTextReader manifestJsonReader = new JsonTextReader(manifestStringReader))
            {
                JObject jsonManifest = (JObject)JToken.ReadFrom(manifestJsonReader);

                var manifestLocation = Convert.ToString(jsonManifest["ManifestLocation"]);
                var updatedManifestLocation = manifestLocation.Replace(appData.InstallLocation, destinationPath);
                jsonManifest["ManifestLocation"] = updatedManifestLocation;

                var installLocation = Convert.ToString(jsonManifest["InstallLocation"]);
                var updatedInstallLocation = installLocation.Replace(appData.InstallLocation, destinationPath);
                jsonManifest["InstallLocation"] = updatedInstallLocation;

                var stagingLocation = Convert.ToString(jsonManifest["StagingLocation"]);
                var updatedStagingLocation = installLocation.Replace(appData.InstallLocation, destinationPath);
                jsonManifest["StagingLocation"] = updatedStagingLocation;

                // write JSON directly to a file
                using (StreamWriter manifestStreamWriter = File.CreateText(appData.ManifestPath))
                using (JsonTextWriter manifestJsonWriter = new JsonTextWriter(manifestStreamWriter))
                {
                    manifestJsonWriter.Formatting = Formatting.Indented;
                    jsonManifest.WriteTo(manifestJsonWriter);
                }
            }
        }

        static FindGameResult FindMatchingGame(IEnumerable<AppData> appDatabase, string partialDisplayName)
        {
            var matchingGames = appDatabase.Where(appData => appData.DisplayName.ToLower().Contains(partialDisplayName.ToLower()));
            if (matchingGames.Count() == 0)
                return new FindGameResult() { Tag = FindGameResultType.NotFound };
            else if (matchingGames.Count() > 1)
                return new FindGameResult() { Tag = FindGameResultType.MultipleFound, MatchingAppData = matchingGames };
            return new FindGameResult() { Tag = FindGameResultType.Found, MatchingAppData = matchingGames };
        }

        static FindGameResult FindGameAndAllDLC(IEnumerable<AppData> appDatabase, string mainGameAppName)
        {
            return new FindGameResult() { Tag = FindGameResultType.MultipleFound, MatchingAppData = appDatabase.Where(appData => appData.MainGameAppName == mainGameAppName) };
        }

        static string GetDestinationPath(string destinationRoot, AppData appData)
        {
            return Path.Combine(destinationRoot, appData.MandatoryAppFolderName);
        }

        static void MoveDirectory(Options options, AppData appData, string destinationRoot)
        {
            if (!Directory.Exists(destinationRoot))
                Directory.CreateDirectory(destinationRoot);

            string destinationPath = GetDestinationPath(destinationRoot, appData);
            LogVerbose(options, $"Copying {appData.InstallLocation} to {destinationPath}");
            DirectoryCopy(appData.InstallLocation, destinationPath, copySubDirs: true);
            LogVerbose(options, "Done copying.");

            if (!options.SkipDelete)
            {
                LogVerbose(options, $"Deleting {appData.InstallLocation}");
                Directory.Delete(appData.InstallLocation, recursive: true);
            }
        }

        private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
        {
            // Get the subdirectories for the specified directory.
            DirectoryInfo dir = new DirectoryInfo(sourceDirName);

            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException(
                    "Source directory does not exist or could not be found: "
                    + sourceDirName);
            }

            DirectoryInfo[] dirs = dir.GetDirectories();

            // If the destination directory doesn't exist, create it.       
            Directory.CreateDirectory(destDirName);

            // Get the files in the directory and copy them to the new location.
            FileInfo[] files = dir.GetFiles();
            foreach (FileInfo file in files)
            {
                string tempPath = Path.Combine(destDirName, file.Name);
                file.CopyTo(tempPath, false);
            }

            // If copying subdirectories, copy them and their contents to new location.
            if (copySubDirs)
            {
                foreach (DirectoryInfo subdir in dirs)
                {
                    string tempPath = Path.Combine(destDirName, subdir.Name);
                    DirectoryCopy(subdir.FullName, tempPath, copySubDirs);
                }
            }
        }
    }
}
