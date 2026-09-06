using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Net.Http;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace GameWatchService
{
    public class Worker(ILogger<Worker> logger) : BackgroundService
    {
        private readonly Dictionary<string, ActiveGame> activeGames =
            new(StringComparer.OrdinalIgnoreCase);

        private List<InstalledGame> installedGames = new();

        // Xbox / PC Game Pass games can start a bootstrap executable from
        // MicrosoftGame.config and then hand off to another gameplay EXE
        // deeper in the Content folder (for example *-WinGDK-Shipping.exe).
        // Cache every executable name found under each Xbox game's Content
        // folder so detection still works even when Windows blocks access to
        // Process.MainModule for a packaged/GDK process.
        private static readonly Dictionary<string, HashSet<string>> xboxExecutableNamesByInstallPath =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool IsSharedXboxHelperProcess(
            string processName)
        {
            if (string.IsNullOrWhiteSpace(
                processName))
            {
                return true;
            }

            string normalized =
                processName.Trim();

            string[] sharedHelpers =
            {
                "gamelaunchhelper",
                "GamingServices",
                "GamingServicesNet",
                "XboxPcApp",
                "XboxPcTray",
                "XboxGameBar",
                "GameBar",
                "GameBarFTServer",
                "GameBarPresenceWriter",
                "Microsoft.GamingApp",
                "EasyAntiCheat",
                "EasyAntiCheat_EOS",
                "EasyAntiCheat_EOS_Setup",
                "BEService",
                "BEService_x64",
                "CrashReportClient",
                "UnrealCEFSubProcess",
                "RobloxCrashHandler"
            };

            return sharedHelpers.Any(
                helper =>
                    normalized.Equals(
                        helper,
                        StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(
                        helper + "-",
                        StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(
                        helper + "_",
                        StringComparison.OrdinalIgnoreCase));
        }

        private DateTime lastGameRefresh =
            DateTime.MinValue;

        private string databasePath =
            string.Empty;

        // Launcher labels discovered dynamically while a game is running.
        // This is used for games such as Minecraft where the same Java
        // process can come from several different launchers.
        private readonly Dictionary<string, string> detectedLauncherOverrides =
            new(StringComparer.OrdinalIgnoreCase);

        // Roblox integration state. Roblox writes the current place/universe
        // information into its local player log while an experience is open.
        private static readonly HttpClient robloxHttpClient =
            new()
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

        private readonly Dictionary<long, string> robloxExperienceNameCache =
            new();

        private string? robloxCurrentLogPath;
        private long robloxCurrentLogPosition;
        private long? robloxCurrentPlaceId;
        private long? robloxCurrentUniverseId;
        private string? robloxCurrentGameName;
        private DateTime robloxLastResolveAttempt = DateTime.MinValue;

        // Apps that should never count as games.
        private static readonly HashSet<string> IgnoredGameNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Wallpaper Engine",

                "Steam",
                "Steam Client",

                "Epic Games Launcher",

                "Riot Client",

                "Battle.net",

                "EA app",
                "EA",
                "EA Desktop",
                "Origin",

                "Ubisoft Connect",
                "Ubisoft Game Launcher",

                "Minecraft Launcher",
                "CurseForge",
                "Prism Launcher",
                "Modrinth",
                "ATLauncher",
                "GDLauncher",
                "MultiMC"
            };

        // =========================================================
        // MAIN LOOP
        // =========================================================

        protected override async Task ExecuteAsync(
            CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "GameWatch has started.");

            string dataDirectory =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "GameWatch");

            Directory.CreateDirectory(
                dataDirectory);

            databasePath =
                Path.Combine(
                    dataDirectory,
                    "GameWatch.db");

            InitializeDatabase();
            ClearActiveSessionFile();

            while (!stoppingToken.IsCancellationRequested)
            {
                if ((DateTime.Now - lastGameRefresh)
                    .TotalMinutes >= 1)
                {
                    RefreshInstalledGames();
                }

                detectedLauncherOverrides.Clear();

                HashSet<string> runningGames =
                    DetectRunningInstalledGames(
                        installedGames);

                DetectKnownRiotProcesses(
                    runningGames);

                DetectKnownBattleNetProcesses(
                    runningGames);

                DetectMinecraftProcesses(
                    runningGames);

                DetectRobloxProcesses(
                    runningGames);

                LoadManualGames(
                    runningGames);

                RemoveIgnoredGames(
                    runningGames);

                DetectStartedGames(
                    runningGames);

                DetectClosedGames(
                    runningGames);

                UpdateActiveSessionFile();

                await Task.Delay(
                    2000,
                    stoppingToken);
            }
        }

        private void RefreshInstalledGames()
        {
            List<InstalledGame> steamGames =
                LoadSteamGames();

            List<InstalledGame> epicGames =
                LoadEpicGames();

            List<InstalledGame> riotGames =
                LoadRiotGames();

            List<InstalledGame> eaGames =
                LoadEAGames();

            List<InstalledGame> ubisoftGames =
                LoadUbisoftGames();

            List<InstalledGame> xboxGames =
                LoadXboxGames();

            installedGames =
                steamGames
                    .Concat(epicGames)
                    .Concat(riotGames)
                    .Concat(eaGames)
                    .Concat(ubisoftGames)
                    .Concat(xboxGames)
                    .Where(
                        g => !IgnoredGameNames.Contains(
                            g.Name))
                    .GroupBy(
                        g => g.InstallPath,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(
                        g => g.First())
                    .ToList();

            lastGameRefresh =
                DateTime.Now;

            logger.LogInformation(
                "Game libraries refreshed. " +
                "Steam: {steam}, Epic: {epic}, Riot: {riot}, " +
                "EA: {ea}, Ubisoft: {ubisoft}, Xbox: {xbox}",
                steamGames.Count,
                epicGames.Count,
                riotGames.Count,
                eaGames.Count,
                ubisoftGames.Count,
                xboxGames.Count);
        }

        // =========================================================
        // SESSION START / STOP
        // =========================================================

        private void DetectStartedGames(
            HashSet<string> runningGames)
        {
            foreach (string gameName in runningGames)
            {
                if (!activeGames.ContainsKey(
                    gameName))
                {
                    string launcher =
                        GetLauncherForGame(
                            gameName);

                    activeGames[gameName] =
                        new ActiveGame(
                            DateTime.Now,
                            launcher);

                    logger.LogInformation(
                        "{game} started at {time} via {launcher}",
                        gameName,
                        activeGames[gameName].StartTime,
                        launcher);
                }
            }
        }

        private void DetectClosedGames(
            HashSet<string> runningGames)
        {
            foreach (string gameName
                in activeGames.Keys.ToList())
            {
                if (!runningGames.Contains(
                    gameName))
                {
                    DateTime endTime =
                        DateTime.Now;

                    ActiveGame activeGame =
                        activeGames[gameName];

                    DateTime startTime =
                        activeGame.StartTime;

                    TimeSpan sessionLength =
                        endTime - startTime;

                    logger.LogInformation(
                        "{game} closed at {time}. " +
                        "Session length: {length}. Launcher: {launcher}",
                        gameName,
                        endTime,
                        sessionLength,
                        activeGame.Launcher);

                    SaveSessionToDatabase(
                        gameName,
                        activeGame.Launcher,
                        startTime,
                        endTime,
                        sessionLength);

                    activeGames.Remove(
                        gameName);
                }
            }
        }

        private string GetLauncherForGame(
            string gameName)
        {
            if (detectedLauncherOverrides.TryGetValue(
                gameName,
                out string? launcherOverride))
            {
                return launcherOverride;
            }

            InstalledGame? installedGame =
                installedGames
                    .FirstOrDefault(
                        game =>
                            game.Name.Equals(
                                gameName,
                                StringComparison.OrdinalIgnoreCase));

            if (installedGame != null)
            {
                return installedGame.Launcher;
            }

            if (gameName.Equals(
                    "VALORANT",
                    StringComparison.OrdinalIgnoreCase) ||
                gameName.Equals(
                    "League of Legends",
                    StringComparison.OrdinalIgnoreCase))
            {
                return "Riot";
            }

            string[] battleNetGames =
            {
                "Overwatch 2",
                "Diablo IV",
                "World of Warcraft",
                "Hearthstone",
                "StarCraft II"
            };

            if (battleNetGames.Contains(
                gameName,
                StringComparer.OrdinalIgnoreCase))
            {
                return "Battle.net";
            }

            return "Manual";
        }

        // =========================================================
        // SQLITE DATABASE
        // =========================================================

        private void InitializeDatabase()
        {
            using SqliteConnection connection =
                new(
                    $"Data Source={databasePath}");

            connection.Open();

            string createTableSql =
                """
                CREATE TABLE IF NOT EXISTS Sessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    GameName TEXT NOT NULL,
                    Launcher TEXT NOT NULL DEFAULT 'Unknown',
                    StartTime TEXT NOT NULL,
                    EndTime TEXT NOT NULL,
                    DurationSeconds INTEGER NOT NULL
                );
                """;

            using (
                SqliteCommand command =
                    new(
                        createTableSql,
                        connection))
            {
                command.ExecuteNonQuery();
            }

            if (!SessionTableHasColumn(
                connection,
                "Launcher"))
            {
                const string addLauncherSql =
                    """
                    ALTER TABLE Sessions
                    ADD COLUMN Launcher TEXT NOT NULL DEFAULT 'Unknown';
                    """;

                using SqliteCommand addLauncherCommand =
                    new(
                        addLauncherSql,
                        connection);

                addLauncherCommand.ExecuteNonQuery();

                logger.LogInformation(
                    "Added Launcher column to existing Sessions table.");
            }

            logger.LogInformation(
                "SQLite database initialized at {path}",
                databasePath);
        }

        private static bool SessionTableHasColumn(
            SqliteConnection connection,
            string columnName)
        {
            using SqliteCommand command =
                connection.CreateCommand();

            command.CommandText =
                "PRAGMA table_info(Sessions);";

            using SqliteDataReader reader =
                command.ExecuteReader();

            while (reader.Read())
            {
                string existingColumnName =
                    reader.GetString(1);

                if (existingColumnName.Equals(
                    columnName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void SaveSessionToDatabase(
            string gameName,
            string launcher,
            DateTime startTime,
            DateTime endTime,
            TimeSpan sessionLength)
        {
            using SqliteConnection connection =
                new(
                    $"Data Source={databasePath}");

            connection.Open();

            string insertSql =
                """
                INSERT INTO Sessions
                (
                    GameName,
                    Launcher,
                    StartTime,
                    EndTime,
                    DurationSeconds
                )
                VALUES
                (
                    $gameName,
                    $launcher,
                    $startTime,
                    $endTime,
                    $durationSeconds
                );
                """;

            using SqliteCommand command =
                new(
                    insertSql,
                    connection);

            command.Parameters.AddWithValue(
                "$gameName",
                gameName);

            command.Parameters.AddWithValue(
                "$launcher",
                launcher);

            command.Parameters.AddWithValue(
                "$startTime",
                startTime.ToString("o"));

            command.Parameters.AddWithValue(
                "$endTime",
                endTime.ToString("o"));

            command.Parameters.AddWithValue(
                "$durationSeconds",
                (int)sessionLength.TotalSeconds);

            command.ExecuteNonQuery();

            logger.LogInformation(
                "Saved {game} session to SQLite with launcher {launcher}.",
                gameName,
                launcher);
        }

        // =========================================================
        // STEAM
        // =========================================================

        private List<InstalledGame> LoadSteamGames()
        {
            List<InstalledGame> games =
                new();

            string? steamRoot =
                FindSteamFolder();

            if (steamRoot == null)
            {
                return games;
            }

            List<string> libraries =
                new()
                {
                    steamRoot
                };

            string libraryFile =
                Path.Combine(
                    steamRoot,
                    "steamapps",
                    "libraryfolders.vdf");

            if (File.Exists(
                libraryFile))
            {
                try
                {
                    string content =
                        File.ReadAllText(
                            libraryFile);

                    MatchCollection matches =
                        Regex.Matches(
                            content,
                            "\"path\"\\s*\"([^\"]+)\"",
                            RegexOptions.IgnoreCase);

                    foreach (Match match
                        in matches)
                    {
                        string libraryPath =
                            match
                                .Groups[1]
                                .Value
                                .Replace(
                                    @"\\",
                                    @"\");

                        if (Directory.Exists(
                                libraryPath) &&
                            !libraries.Contains(
                                libraryPath,
                                StringComparer.OrdinalIgnoreCase))
                        {
                            libraries.Add(
                                libraryPath);
                        }
                    }
                }
                catch
                {
                }
            }

            foreach (string library
                in libraries)
            {
                string steamApps =
                    Path.Combine(
                        library,
                        "steamapps");

                if (!Directory.Exists(
                    steamApps))
                {
                    continue;
                }

                string[] manifests;

                try
                {
                    manifests =
                        Directory.GetFiles(
                            steamApps,
                            "appmanifest_*.acf");
                }
                catch
                {
                    continue;
                }

                foreach (string manifest
                    in manifests)
                {
                    try
                    {
                        string content =
                            File.ReadAllText(
                                manifest);

                        Match nameMatch =
                            Regex.Match(
                                content,
                                "\"name\"\\s*\"([^\"]+)\"",
                                RegexOptions.IgnoreCase);

                        Match installMatch =
                            Regex.Match(
                                content,
                                "\"installdir\"\\s*\"([^\"]+)\"",
                                RegexOptions.IgnoreCase);

                        if (!nameMatch.Success ||
                            !installMatch.Success)
                        {
                            continue;
                        }

                        string gameName =
                            nameMatch.Groups[1].Value;

                        string installDirectory =
                            installMatch.Groups[1].Value;

                        string fullGamePath =
                            Path.Combine(
                                steamApps,
                                "common",
                                installDirectory);

                        if (Directory.Exists(
                            fullGamePath))
                        {
                            games.Add(
                                new InstalledGame(
                                    gameName,
                                    fullGamePath,
                                    "Steam"));
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return games;
        }

        private static string? FindSteamFolder()
        {
            List<string> possiblePaths =
                new();

            try
            {
                using RegistryKey? key =
                    Registry.LocalMachine
                        .OpenSubKey(
                            @"SOFTWARE\WOW6432Node\Valve\Steam");

                string? path =
                    key?
                        .GetValue(
                            "InstallPath")
                        ?.ToString();

                if (!string.IsNullOrWhiteSpace(
                    path))
                {
                    possiblePaths.Add(
                        path);
                }
            }
            catch
            {
            }

            string programFilesX86 =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86);

            string programFiles =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles);

            possiblePaths.Add(
                Path.Combine(
                    programFilesX86,
                    "Steam"));

            possiblePaths.Add(
                Path.Combine(
                    programFiles,
                    "Steam"));

            foreach (string path
                in possiblePaths)
            {
                if (Directory.Exists(
                    Path.Combine(
                        path,
                        "steamapps")))
                {
                    return path;
                }
            }

            return null;
        }

        // =========================================================
        // EPIC GAMES
        // =========================================================

        private static List<InstalledGame> LoadEpicGames()
        {
            List<InstalledGame> games =
                new();

            string manifestFolder =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "Epic",
                    "EpicGamesLauncher",
                    "Data",
                    "Manifests");

            if (!Directory.Exists(
                manifestFolder))
            {
                return games;
            }

            string[] manifests;

            try
            {
                manifests =
                    Directory.GetFiles(
                        manifestFolder,
                        "*.item");
            }
            catch
            {
                return games;
            }

            foreach (string manifest
                in manifests)
            {
                try
                {
                    string json =
                        File.ReadAllText(
                            manifest);

                    using JsonDocument document =
                        JsonDocument.Parse(
                            json);

                    JsonElement root =
                        document.RootElement;

                    if (!root.TryGetProperty(
                            "DisplayName",
                            out JsonElement nameElement))
                    {
                        continue;
                    }

                    if (!root.TryGetProperty(
                            "InstallLocation",
                            out JsonElement pathElement))
                    {
                        continue;
                    }

                    string? gameName =
                        nameElement.GetString();

                    string? installPath =
                        pathElement.GetString();

                    if (string.IsNullOrWhiteSpace(
                            gameName) ||
                        string.IsNullOrWhiteSpace(
                            installPath))
                    {
                        continue;
                    }

                    if (Directory.Exists(
                        installPath))
                    {
                        games.Add(
                            new InstalledGame(
                                gameName,
                                installPath,
                                "Epic"));
                    }
                }
                catch
                {
                }
            }

            return games;
        }

        // =========================================================
        // RIOT
        // =========================================================

        private static List<InstalledGame> LoadRiotGames()
        {
            List<InstalledGame> games =
                new();

            AddKnownInstall(
                games,
                "VALORANT",
                @"C:\Riot Games\VALORANT",
                "Riot");

            AddKnownInstall(
                games,
                "League of Legends",
                @"C:\Riot Games\League of Legends",
                "Riot");

            return games;
        }

        private static void DetectKnownRiotProcesses(
            HashSet<string> runningGames)
        {
            Process[] processes =
                Process.GetProcesses();

            foreach (Process process
                in processes)
            {
                try
                {
                    string processName =
                        process.ProcessName;

                    if (processName.Contains(
                        "VALORANT",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        runningGames.Add(
                            "VALORANT");
                    }

                    if (processName.Contains(
                        "League of Legends",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        runningGames.Add(
                            "League of Legends");
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        // =========================================================
        // BATTLE.NET
        // =========================================================

        private static void DetectKnownBattleNetProcesses(
            HashSet<string> runningGames)
        {
            Process[] processes =
                Process.GetProcesses();

            foreach (Process process
                in processes)
            {
                try
                {
                    string name =
                        process.ProcessName;

                    if (name.Equals(
                        "Overwatch",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        runningGames.Add(
                            "Overwatch 2");
                    }

                    if (name.Equals(
                            "Diablo IV",
                            StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(
                            "DiabloIV",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        runningGames.Add(
                            "Diablo IV");
                    }

                    if (name.Equals(
                            "Wow",
                            StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(
                            "WowClassic",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        runningGames.Add(
                            "World of Warcraft");
                    }

                    if (name.Equals(
                        "Hearthstone",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        runningGames.Add(
                            "Hearthstone");
                    }

                    if (name.StartsWith(
                        "SC2",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        runningGames.Add(
                            "StarCraft II");
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        // =========================================================
        // MINECRAFT / MODDED MINECRAFT
        // =========================================================

        private void DetectMinecraftProcesses(
            HashSet<string> runningGames)
        {
            Process[] processes =
                Process.GetProcesses();

            string fallbackLauncher =
                DetectRunningMinecraftLauncher(
                    processes);

            foreach (Process process in processes)
            {
                try
                {
                    string processName =
                        process.ProcessName;

                    // Minecraft for Windows / Bedrock Edition.
                    if (processName.Equals(
                            "Minecraft.Windows",
                            StringComparison.OrdinalIgnoreCase) ||
                        processName.Equals(
                            "Minecraft",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        AddDetectedGame(
                            runningGames,
                            "Minecraft",
                            "Minecraft");

                        continue;
                    }

                    // Minecraft Java Edition normally runs as javaw.exe.
                    if (!processName.Equals(
                            "javaw",
                            StringComparison.OrdinalIgnoreCase) &&
                        !processName.Equals(
                            "java",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? commandLine =
                        TryGetProcessCommandLine(
                            process);

                    if (!LooksLikeMinecraftCommandLine(
                        commandLine))
                    {
                        continue;
                    }

                    string? gameDirectory =
                        ExtractMinecraftGameDirectory(
                            commandLine!);

                    (string gameName, string launcher) =
                        GetMinecraftIdentity(
                            gameDirectory,
                            fallbackLauncher);

                    AddDetectedGame(
                        runningGames,
                        gameName,
                        launcher);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private void AddDetectedGame(
            HashSet<string> runningGames,
            string gameName,
            string launcher)
        {
            runningGames.Add(
                gameName);

            detectedLauncherOverrides[gameName] =
                launcher;
        }

        private static string DetectRunningMinecraftLauncher(
            IEnumerable<Process> processes)
        {
            foreach (Process process in processes)
            {
                try
                {
                    string name =
                        process.ProcessName;

                    if (name.Contains(
                            "CurseForge",
                            StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(
                            "Overwolf",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return "CurseForge";
                    }

                    if (name.Contains(
                        "PrismLauncher",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return "Prism";
                    }

                    if (name.Contains(
                            "Modrinth",
                            StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(
                            "Theseus",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return "Modrinth";
                    }

                    if (name.Contains(
                        "ATLauncher",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return "ATLauncher";
                    }

                    if (name.Contains(
                            "GDLauncher",
                            StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(
                            "gdlauncher",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return "GDLauncher";
                    }

                    if (name.Contains(
                        "MultiMC",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return "MultiMC";
                    }

                    if (name.Contains(
                            "MinecraftLauncher",
                            StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(
                            "Minecraft Launcher",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return "Minecraft";
                    }
                }
                catch
                {
                }
            }

            return "Minecraft";
        }

        private static bool LooksLikeMinecraftCommandLine(
            string? commandLine)
        {
            if (string.IsNullOrWhiteSpace(
                commandLine))
            {
                return false;
            }

            return
                commandLine.Contains(
                    "minecraft",
                    StringComparison.OrdinalIgnoreCase) ||
                commandLine.Contains(
                    "mojang",
                    StringComparison.OrdinalIgnoreCase) ||
                commandLine.Contains(
                    "--gameDir",
                    StringComparison.OrdinalIgnoreCase) ||
                commandLine.Contains(
                    "fabricmc",
                    StringComparison.OrdinalIgnoreCase) ||
                commandLine.Contains(
                    "neoforge",
                    StringComparison.OrdinalIgnoreCase) ||
                commandLine.Contains(
                    "modlauncher",
                    StringComparison.OrdinalIgnoreCase) ||
                commandLine.Contains(
                    "--launchTarget",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string? ExtractMinecraftGameDirectory(
            string commandLine)
        {
            Match gameDirMatch =
                Regex.Match(
                    commandLine,
                    "(?:--gameDir|--gameDirectory)(?:=|\\s+)(?:\\\"(?<path>[^\\\"]+)\\\"|(?<path>\\S+))",
                    RegexOptions.IgnoreCase);

            if (!gameDirMatch.Success)
            {
                return null;
            }

            string gameDirectory =
                gameDirMatch
                    .Groups["path"]
                    .Value
                    .Trim();

            if (string.IsNullOrWhiteSpace(
                gameDirectory))
            {
                return null;
            }

            return gameDirectory;
        }

        private static (string GameName, string Launcher) GetMinecraftIdentity(
            string? gameDirectory,
            string fallbackLauncher)
        {
            if (string.IsNullOrWhiteSpace(
                gameDirectory))
            {
                return (
                    "Minecraft",
                    fallbackLauncher);
            }

            string normalizedPath =
                gameDirectory
                    .Replace('/', '\\')
                    .TrimEnd('\\');

            string? instanceName;

            instanceName =
                GetPathSegmentAfterMarker(
                    normalizedPath,
                    "\\curseforge\\minecraft\\Instances\\");

            if (!string.IsNullOrWhiteSpace(
                instanceName))
            {
                return (
                    BuildMinecraftDisplayName(
                        instanceName),
                    "CurseForge");
            }

            instanceName =
                GetPathSegmentAfterMarker(
                    normalizedPath,
                    "\\PrismLauncher\\instances\\");

            if (!string.IsNullOrWhiteSpace(
                instanceName))
            {
                return (
                    BuildMinecraftDisplayName(
                        instanceName),
                    "Prism");
            }

            instanceName =
                GetPathSegmentAfterMarker(
                    normalizedPath,
                    "\\MultiMC\\instances\\");

            if (!string.IsNullOrWhiteSpace(
                instanceName))
            {
                return (
                    BuildMinecraftDisplayName(
                        instanceName),
                    "MultiMC");
            }

            instanceName =
                GetPathSegmentAfterMarker(
                    normalizedPath,
                    "\\ATLauncher\\instances\\");

            if (!string.IsNullOrWhiteSpace(
                instanceName))
            {
                return (
                    BuildMinecraftDisplayName(
                        instanceName),
                    "ATLauncher");
            }

            instanceName =
                GetPathSegmentAfterMarker(
                    normalizedPath,
                    "\\GDLauncher\\instances\\");

            if (string.IsNullOrWhiteSpace(
                instanceName))
            {
                instanceName =
                    GetPathSegmentAfterMarker(
                        normalizedPath,
                        "\\gdlauncher_carbon\\data\\instances\\");
            }

            if (!string.IsNullOrWhiteSpace(
                instanceName))
            {
                return (
                    BuildMinecraftDisplayName(
                        instanceName),
                    "GDLauncher");
            }

            instanceName =
                GetPathSegmentAfterMarker(
                    normalizedPath,
                    "\\com.modrinth.theseus\\profiles\\");

            if (string.IsNullOrWhiteSpace(
                instanceName))
            {
                instanceName =
                    GetPathSegmentAfterMarker(
                        normalizedPath,
                        "\\ModrinthApp\\profiles\\");
            }

            if (!string.IsNullOrWhiteSpace(
                instanceName))
            {
                return (
                    BuildMinecraftDisplayName(
                        instanceName),
                    "Modrinth");
            }

            // The normal official Java install usually points at .minecraft.
            if (normalizedPath.EndsWith(
                    "\\.minecraft",
                    StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.Contains(
                    "\\.minecraft\\",
                    StringComparison.OrdinalIgnoreCase))
            {
                return (
                    "Minecraft",
                    "Minecraft");
            }

            // A custom game directory may still belong to a modded launcher.
            // Use the folder name as the instance name only when we already
            // recognized a non-default launcher process.
            if (!fallbackLauncher.Equals(
                    "Minecraft",
                    StringComparison.OrdinalIgnoreCase))
            {
                string folderName =
                    new DirectoryInfo(
                            normalizedPath)
                        .Name;

                if (IsUsefulMinecraftInstanceName(
                    folderName))
                {
                    return (
                        BuildMinecraftDisplayName(
                            folderName),
                        fallbackLauncher);
                }
            }

            return (
                "Minecraft",
                fallbackLauncher);
        }

        private static string? GetPathSegmentAfterMarker(
            string path,
            string marker)
        {
            int markerIndex =
                path.IndexOf(
                    marker,
                    StringComparison.OrdinalIgnoreCase);

            if (markerIndex < 0)
            {
                return null;
            }

            int startIndex =
                markerIndex + marker.Length;

            if (startIndex >= path.Length)
            {
                return null;
            }

            string remaining =
                path[startIndex..]
                    .TrimStart('\\');

            int separatorIndex =
                remaining.IndexOf('\\');

            string instanceName =
                separatorIndex >= 0
                    ? remaining[..separatorIndex]
                    : remaining;

            return IsUsefulMinecraftInstanceName(
                    instanceName)
                ? instanceName
                : null;
        }

        private static bool IsUsefulMinecraftInstanceName(
            string? instanceName)
        {
            if (string.IsNullOrWhiteSpace(
                instanceName))
            {
                return false;
            }

            string trimmed =
                instanceName.Trim();

            return
                !trimmed.Equals(
                    ".minecraft",
                    StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Equals(
                    "minecraft",
                    StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Equals(
                    "instances",
                    StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Equals(
                    "profiles",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildMinecraftDisplayName(
            string instanceName)
        {
            string cleanedInstanceName =
                instanceName
                    .Replace('_', ' ')
                    .Trim();

            return
                $"Minecraft - {cleanedInstanceName}";
        }

        private static string? TryGetProcessCommandLine(
            Process process)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            const int processCommandLineInformation =
                60;

            IntPtr buffer =
                IntPtr.Zero;

            try
            {
                _ = NtQueryInformationProcess(
                    process.Handle,
                    processCommandLineInformation,
                    IntPtr.Zero,
                    0,
                    out int requiredLength);

                if (requiredLength <= 0)
                {
                    return null;
                }

                buffer =
                    Marshal.AllocHGlobal(
                        requiredLength);

                int status =
                    NtQueryInformationProcess(
                        process.Handle,
                        processCommandLineInformation,
                        buffer,
                        requiredLength,
                        out _);

                if (status != 0)
                {
                    return null;
                }

                ushort stringLength =
                    unchecked(
                        (ushort)Marshal.ReadInt16(
                            buffer,
                            0));

                if (stringLength == 0)
                {
                    return string.Empty;
                }

                int pointerOffset =
                    IntPtr.Size == 8
                        ? 8
                        : 4;

                IntPtr stringPointer =
                    Marshal.ReadIntPtr(
                        buffer,
                        pointerOffset);

                if (stringPointer == IntPtr.Zero)
                {
                    return null;
                }

                return Marshal.PtrToStringUni(
                    stringPointer,
                    stringLength / 2);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(
                        buffer);
                }
            }
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            IntPtr processInformation,
            int processInformationLength,
            out int returnLength);

        // =========================================================
        // ROBLOX
        // =========================================================

        private void DetectRobloxProcesses(
            HashSet<string> runningGames)
        {
            Process[] processes =
                Process.GetProcesses();

            Process? robloxProcess =
                null;

            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        string processName =
                            process.ProcessName;

                        if (IsRobloxPlayerProcess(
                            processName))
                        {
                            robloxProcess =
                                process;

                            break;
                        }
                    }
                    catch
                    {
                    }
                }

                if (robloxProcess == null)
                {
                    ResetRobloxTrackingState();
                    return;
                }

                string? logDirectory =
                    FindRobloxLogDirectory(
                        robloxProcess);

                if (!string.IsNullOrWhiteSpace(
                        logDirectory))
                {
                    UpdateRobloxStateFromLogs(
                        logDirectory);
                }

                // If the log tells us which experience is open, use its
                // real Roblox title. If Roblox is running but the log is
                // temporarily unavailable, still track it as Roblox.
                string gameName =
                    robloxCurrentGameName ??
                    "Roblox";

                AddDetectedGame(
                    runningGames,
                    gameName,
                    "Roblox");
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        private static bool IsRobloxPlayerProcess(
            string processName)
        {
            return
                processName.Equals(
                    "RobloxPlayerBeta",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals(
                    "RobloxPlayer",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals(
                    "Windows10Universal",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string? FindRobloxLogDirectory(
            Process robloxProcess)
        {
            try
            {
                string? executablePath =
                    robloxProcess.MainModule
                        ?.FileName;

                if (!string.IsNullOrWhiteSpace(
                        executablePath))
                {
                    Match userProfileMatch =
                        Regex.Match(
                            executablePath,
                            @"^([A-Za-z]:\\Users\\[^\\]+)\\",
                            RegexOptions.IgnoreCase);

                    if (userProfileMatch.Success)
                    {
                        string candidate =
                            Path.Combine(
                                userProfileMatch.Groups[1].Value,
                                "AppData",
                                "Local",
                                "Roblox",
                                "logs");

                        if (Directory.Exists(
                            candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch
            {
            }

            // Fallback for unusual Roblox/Bloxstrap install locations.
            // The Windows service runs as LocalSystem, so LocalApplicationData
            // is not the signed-in player's AppData. Search normal user
            // profiles instead.
            try
            {
                string usersRoot =
                    Path.Combine(
                        Path.GetPathRoot(
                            Environment.SystemDirectory) ??
                            @"C:\",
                        "Users");

                if (!Directory.Exists(
                    usersRoot))
                {
                    return null;
                }

                return Directory
                    .GetDirectories(
                        usersRoot)
                    .Select(
                        profile =>
                            Path.Combine(
                                profile,
                                "AppData",
                                "Local",
                                "Roblox",
                                "logs"))
                    .Where(
                        Directory.Exists)
                    .OrderByDescending(
                        GetNewestRobloxLogWriteTime)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static DateTime GetNewestRobloxLogWriteTime(
            string logDirectory)
        {
            try
            {
                FileInfo? newest =
                    new DirectoryInfo(
                        logDirectory)
                    .GetFiles()
                    .Where(
                        file =>
                            file.Name.Contains(
                                "Player",
                                StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(
                        file => file.LastWriteTimeUtc)
                    .FirstOrDefault();

                return newest?.LastWriteTimeUtc ??
                    DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private void UpdateRobloxStateFromLogs(
            string logDirectory)
        {
            FileInfo? newestLog;

            try
            {
                newestLog =
                    new DirectoryInfo(
                        logDirectory)
                    .GetFiles()
                    .Where(
                        file =>
                            file.Name.Contains(
                                "Player",
                                StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(
                        file => file.CreationTimeUtc)
                    .FirstOrDefault();
            }
            catch
            {
                return;
            }

            if (newestLog == null)
            {
                return;
            }

            if (!string.Equals(
                    robloxCurrentLogPath,
                    newestLog.FullName,
                    StringComparison.OrdinalIgnoreCase))
            {
                robloxCurrentLogPath =
                    newestLog.FullName;

                robloxCurrentLogPosition =
                    0;

                robloxCurrentPlaceId =
                    null;

                robloxCurrentUniverseId =
                    null;

                robloxCurrentGameName =
                    null;
            }

            try
            {
                using FileStream stream =
                    new(
                        newestLog.FullName,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                if (robloxCurrentLogPosition >
                    stream.Length)
                {
                    robloxCurrentLogPosition =
                        0;
                }

                stream.Seek(
                    robloxCurrentLogPosition,
                    SeekOrigin.Begin);

                using StreamReader reader =
                    new(
                        stream,
                        leaveOpen: true);

                string? line;

                while ((line = reader.ReadLine()) != null)
                {
                    ParseRobloxLogLine(
                        line);
                }

                robloxCurrentLogPosition =
                    stream.Position;
            }
            catch
            {
                return;
            }

            ResolveCurrentRobloxExperienceName();
        }

        private void ParseRobloxLogLine(
            string line)
        {
            Match joiningMatch =
                Regex.Match(
                    line,
                    @"! Joining game '[0-9a-f\-]{36}' place ([0-9]+) at ",
                    RegexOptions.IgnoreCase);

            if (joiningMatch.Success &&
                long.TryParse(
                    joiningMatch.Groups[1].Value,
                    out long placeId))
            {
                robloxCurrentPlaceId =
                    placeId;

                robloxCurrentUniverseId =
                    null;

                robloxCurrentGameName =
                    null;

                robloxLastResolveAttempt =
                    DateTime.MinValue;

                return;
            }

            Match universeMatch =
                Regex.Match(
                    line,
                    @"universeid:([0-9]+)",
                    RegexOptions.IgnoreCase);

            if (universeMatch.Success &&
                long.TryParse(
                    universeMatch.Groups[1].Value,
                    out long universeId))
            {
                robloxCurrentUniverseId =
                    universeId;

                robloxLastResolveAttempt =
                    DateTime.MinValue;
            }

            if (line.Contains(
                    "[FLog::Network] Time to disconnect replication data:",
                    StringComparison.OrdinalIgnoreCase) ||
                line.Contains(
                    "[FLog::SingleSurfaceApp] leaveUGCGameInternal",
                    StringComparison.OrdinalIgnoreCase))
            {
                robloxCurrentPlaceId =
                    null;

                robloxCurrentUniverseId =
                    null;

                robloxCurrentGameName =
                    null;
            }
        }

        private void ResolveCurrentRobloxExperienceName()
        {
            if (!robloxCurrentPlaceId.HasValue)
            {
                return;
            }

            if (robloxCurrentGameName != null)
            {
                return;
            }

            if ((DateTime.Now - robloxLastResolveAttempt)
                .TotalSeconds < 30)
            {
                return;
            }

            robloxLastResolveAttempt =
                DateTime.Now;

            try
            {
                long? universeId =
                    robloxCurrentUniverseId;

                if (!universeId.HasValue)
                {
                    string universeJson =
                        robloxHttpClient
                            .GetStringAsync(
                                $"https://apis.roblox.com/universes/v1/places/{robloxCurrentPlaceId.Value}/universe")
                            .GetAwaiter()
                            .GetResult();

                    using JsonDocument universeDocument =
                        JsonDocument.Parse(
                            universeJson);

                    if (universeDocument.RootElement.TryGetProperty(
                            "universeId",
                            out JsonElement universeElement) &&
                        universeElement.TryGetInt64(
                            out long resolvedUniverseId))
                    {
                        universeId =
                            resolvedUniverseId;

                        robloxCurrentUniverseId =
                            resolvedUniverseId;
                    }
                }

                if (!universeId.HasValue)
                {
                    return;
                }

                if (robloxExperienceNameCache.TryGetValue(
                        universeId.Value,
                        out string? cachedName))
                {
                    robloxCurrentGameName =
                        BuildRobloxDisplayName(
                            cachedName);

                    return;
                }

                string gameJson =
                    robloxHttpClient
                        .GetStringAsync(
                            $"https://games.roblox.com/v1/games?universeIds={universeId.Value}")
                        .GetAwaiter()
                        .GetResult();

                using JsonDocument gameDocument =
                    JsonDocument.Parse(
                        gameJson);

                if (!gameDocument.RootElement.TryGetProperty(
                        "data",
                        out JsonElement dataElement) ||
                    dataElement.ValueKind != JsonValueKind.Array ||
                    dataElement.GetArrayLength() == 0)
                {
                    return;
                }

                JsonElement firstGame =
                    dataElement[0];

                if (!firstGame.TryGetProperty(
                        "name",
                        out JsonElement nameElement))
                {
                    return;
                }

                string? experienceName =
                    nameElement.GetString();

                if (string.IsNullOrWhiteSpace(
                    experienceName))
                {
                    return;
                }

                robloxExperienceNameCache[universeId.Value] =
                    experienceName;

                robloxCurrentGameName =
                    BuildRobloxDisplayName(
                        experienceName);
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Could not resolve Roblox experience name.");
            }
        }

        private static string BuildRobloxDisplayName(
            string experienceName)
        {
            string cleanedName =
                experienceName.Trim();

            if (cleanedName.StartsWith(
                    "Roblox - ",
                    StringComparison.OrdinalIgnoreCase))
            {
                return cleanedName;
            }

            return
                $"Roblox - {cleanedName}";
        }

        private void ResetRobloxTrackingState()
        {
            robloxCurrentLogPath =
                null;

            robloxCurrentLogPosition =
                0;

            robloxCurrentPlaceId =
                null;

            robloxCurrentUniverseId =
                null;

            robloxCurrentGameName =
                null;

            robloxLastResolveAttempt =
                DateTime.MinValue;
        }

        // =========================================================
        // EA APP
        // =========================================================

        private static List<InstalledGame> LoadEAGames()
        {
            List<InstalledGame> games =
                new();

            // Look for games registered as installed Windows programs.
            games.AddRange(
                FindGamesByPublisher(
                    "Electronic Arts",
                    "EA"));

            // Common EA App location.
            AddGamesFromFolder(
                games,
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ProgramFiles),
                    "EA Games"),
                "EA");

            // Older Origin installs.
            AddGamesFromFolder(
                games,
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ProgramFilesX86),
                    "Origin Games"),
                "EA");

            return games
                .Where(
                    g => !IgnoredGameNames.Contains(
                        g.Name))
                .GroupBy(
                    g => g.InstallPath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    g => g.First())
                .ToList();
        }

        // =========================================================
        // UBISOFT CONNECT
        // =========================================================

        private static List<InstalledGame> LoadUbisoftGames()
        {
            List<InstalledGame> games =
                new();

            // Windows installed-program data.
            games.AddRange(
                FindGamesByPublisher(
                    "Ubisoft",
                    "Ubisoft"));

            // Ubisoft's own install registry.
            try
            {
                using RegistryKey baseKey =
                    RegistryKey.OpenBaseKey(
                        RegistryHive.LocalMachine,
                        RegistryView.Registry32);

                using RegistryKey? installs =
                    baseKey.OpenSubKey(
                        @"SOFTWARE\Ubisoft\Launcher\Installs");

                if (installs != null)
                {
                    foreach (string subKeyName
                        in installs.GetSubKeyNames())
                    {
                        try
                        {
                            using RegistryKey? gameKey =
                                installs.OpenSubKey(
                                    subKeyName);

                            string? installDirectory =
                                gameKey?
                                    .GetValue(
                                        "InstallDir")
                                    ?.ToString();

                            if (string.IsNullOrWhiteSpace(
                                    installDirectory))
                            {
                                continue;
                            }

                            installDirectory =
                                installDirectory.TrimEnd(
                                    '\\',
                                    '/');

                            if (!Directory.Exists(
                                installDirectory))
                            {
                                continue;
                            }

                            string gameName =
                                new DirectoryInfo(
                                    installDirectory)
                                .Name;

                            games.Add(
                                new InstalledGame(
                                    gameName,
                                    installDirectory,
                                    "Ubisoft"));
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            // Default Ubisoft Connect games folder.
            AddGamesFromFolder(
                games,
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ProgramFilesX86),
                    "Ubisoft",
                    "Ubisoft Game Launcher",
                    "games"),
                "Ubisoft");

            return games
                .Where(
                    g => !IgnoredGameNames.Contains(
                        g.Name))
                .GroupBy(
                    g => g.InstallPath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    g => g.First())
                .ToList();
        }

        // =========================================================
        // XBOX APP / MICROSOFT STORE (GDK)
        // =========================================================

        private static List<InstalledGame> LoadXboxGames()
        {
            List<InstalledGame> games =
                new();

            xboxExecutableNamesByInstallPath.Clear();

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady)
                    {
                        continue;
                    }

                    string xboxRoot =
                        Path.Combine(
                            drive.RootDirectory.FullName,
                            "XboxGames");

                    if (!Directory.Exists(
                        xboxRoot))
                    {
                        continue;
                    }

                    foreach (string gameFolder
                        in Directory.GetDirectories(
                            xboxRoot))
                    {
                        string contentFolder =
                            Path.Combine(
                                gameFolder,
                                "Content");

                        string configPath =
                            Path.Combine(
                                contentFolder,
                                "MicrosoftGame.config");

                        if (!File.Exists(
                            configPath))
                        {
                            continue;
                        }

                        TryAddXboxGameFromConfig(
                            games,
                            contentFolder,
                            configPath);

                        CacheXboxExecutableNames(
                            contentFolder);
                    }
                }
                catch
                {
                }
            }

            return games
                .Where(
                    g => !IgnoredGameNames.Contains(
                        g.Name))
                .GroupBy(
                    g => g.ExecutablePath ?? g.InstallPath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    g => g.First())
                .ToList();
        }

        private static void TryAddXboxGameFromConfig(
            List<InstalledGame> games,
            string contentFolder,
            string configPath)
        {
            try
            {
                XDocument document =
                    XDocument.Load(
                        configPath);

                XElement? root =
                    document.Root;

                if (root == null)
                {
                    return;
                }

                XElement? shellVisuals =
                    root.Element(
                        "ShellVisuals");

                string? displayName =
                    shellVisuals?
                        .Attribute(
                            "DefaultDisplayName")
                        ?.Value;

                if (string.IsNullOrWhiteSpace(
                    displayName))
                {
                    displayName =
                        new DirectoryInfo(
                            Directory.GetParent(
                                contentFolder)!
                                .FullName)
                            .Name;
                }

                // Add the Xbox game from the install root even if the
                // executable listed in MicrosoftGame.config is only a
                // bootstrap/logical entry and does not physically exist.
                // The running-game detector can still identify any real
                // gameplay EXE found underneath this Content folder.
                string? preferredExecutablePath = null;

                XElement? executableList =
                    root.Element(
                        "ExecutableList");

                if (executableList != null)
                {
                    foreach (XElement executableElement
                        in executableList.Elements(
                            "Executable"))
                    {
                        string? relativeExecutablePath =
                            executableElement
                                .Attribute(
                                    "Name")
                                ?.Value;

                        if (string.IsNullOrWhiteSpace(
                            relativeExecutablePath))
                        {
                            continue;
                        }

                        string fullExecutablePath =
                            Path.GetFullPath(
                                Path.Combine(
                                    contentFolder,
                                    relativeExecutablePath
                                        .Replace('/', Path.DirectorySeparatorChar)
                                        .Replace('\\', Path.DirectorySeparatorChar)));

                        if (File.Exists(
                            fullExecutablePath))
                        {
                            preferredExecutablePath =
                                fullExecutablePath;

                            break;
                        }
                    }
                }

                games.Add(
                    new InstalledGame(
                        displayName,
                        contentFolder,
                        "Xbox",
                        preferredExecutablePath));

            }
            catch
            {
            }
        }

        private static void CacheXboxExecutableNames(
            string contentFolder)
        {
            HashSet<string> executableNames =
                new(StringComparer.OrdinalIgnoreCase);

            Stack<string> folders =
                new();

            folders.Push(
                contentFolder);

            while (folders.Count > 0)
            {
                string currentFolder =
                    folders.Pop();

                try
                {
                    foreach (string executablePath
                        in Directory.EnumerateFiles(
                            currentFolder,
                            "*.exe",
                            SearchOption.TopDirectoryOnly))
                    {
                        string executableName =
                            Path.GetFileNameWithoutExtension(
                                executablePath);

                        if (!string.IsNullOrWhiteSpace(
                                executableName) &&
                            !ShouldIgnoreProcess(
                                executableName) &&
                            !IsSharedXboxHelperProcess(
                                executableName))
                        {
                            executableNames.Add(
                                executableName);
                        }
                    }
                }
                catch
                {
                }

                try
                {
                    foreach (string childFolder
                        in Directory.EnumerateDirectories(
                            currentFolder))
                    {
                        folders.Push(
                            childFolder);
                    }
                }
                catch
                {
                }
            }

            xboxExecutableNamesByInstallPath[
                Path.GetFullPath(
                    contentFolder)] = executableNames;

        }

        // =========================================================
        // WINDOWS INSTALLED PROGRAM REGISTRY
        // =========================================================

        private static List<InstalledGame> FindGamesByPublisher(
            string publisherText,
            string launcher)
        {
            List<InstalledGame> games =
                new();

            ReadUninstallRegistry(
                games,
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                publisherText,
                launcher);

            ReadUninstallRegistry(
                games,
                RegistryHive.LocalMachine,
                RegistryView.Registry32,
                publisherText,
                launcher);

            ReadUninstallRegistry(
                games,
                RegistryHive.CurrentUser,
                RegistryView.Registry64,
                publisherText,
                launcher);

            ReadUninstallRegistry(
                games,
                RegistryHive.CurrentUser,
                RegistryView.Registry32,
                publisherText,
                launcher);

            return games;
        }

        private static void ReadUninstallRegistry(
            List<InstalledGame> games,
            RegistryHive hive,
            RegistryView view,
            string publisherText,
            string launcher)
        {
            try
            {
                using RegistryKey baseKey =
                    RegistryKey.OpenBaseKey(
                        hive,
                        view);

                using RegistryKey? uninstallKey =
                    baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");

                if (uninstallKey == null)
                {
                    return;
                }

                foreach (string subKeyName
                    in uninstallKey.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? appKey =
                            uninstallKey.OpenSubKey(
                                subKeyName);

                        string? displayName =
                            appKey?
                                .GetValue(
                                    "DisplayName")
                                ?.ToString();

                        string? publisher =
                            appKey?
                                .GetValue(
                                    "Publisher")
                                ?.ToString();

                        string? installLocation =
                            appKey?
                                .GetValue(
                                    "InstallLocation")
                                ?.ToString();

                        if (string.IsNullOrWhiteSpace(
                                displayName) ||
                            string.IsNullOrWhiteSpace(
                                publisher) ||
                            string.IsNullOrWhiteSpace(
                                installLocation))
                        {
                            continue;
                        }

                        if (!publisher.Contains(
                            publisherText,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        installLocation =
                            installLocation.TrimEnd(
                                '\\',
                                '/');

                        if (!Directory.Exists(
                            installLocation))
                        {
                            continue;
                        }

                        games.Add(
                            new InstalledGame(
                                displayName,
                                installLocation,
                                launcher));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        // =========================================================
        // COMMON GAME FOLDERS
        // =========================================================

        private static void AddGamesFromFolder(
            List<InstalledGame> games,
            string rootFolder,
            string launcher)
        {
            if (!Directory.Exists(
                rootFolder))
            {
                return;
            }

            try
            {
                foreach (string gameFolder
                    in Directory.GetDirectories(
                        rootFolder))
                {
                    string gameName =
                        new DirectoryInfo(
                            gameFolder)
                        .Name;

                    games.Add(
                        new InstalledGame(
                            gameName,
                            gameFolder,
                            launcher));
                }
            }
            catch
            {
            }
        }

        private static void AddKnownInstall(
            List<InstalledGame> games,
            string gameName,
            string installPath,
            string launcher)
        {
            if (Directory.Exists(
                installPath))
            {
                games.Add(
                    new InstalledGame(
                        gameName,
                        installPath,
                        launcher));
            }
        }

        // =========================================================
        // RUNNING INSTALLED GAME DETECTION
        // =========================================================

        private static HashSet<string> DetectRunningInstalledGames(
            List<InstalledGame> games)
        {
            HashSet<string> runningGames =
                new(
                    StringComparer.OrdinalIgnoreCase);

            Process[] processes =
                Process.GetProcesses();

            foreach (Process process
                in processes)
            {
                try
                {
                    string processName;

                    try
                    {
                        processName =
                            process.ProcessName;
                    }
                    catch
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(
                            processName) ||
                        ShouldIgnoreProcess(
                            processName) ||
                        IsSharedXboxHelperProcess(
                            processName))
                    {
                        continue;
                    }

                    // Some Xbox/GDK processes do not allow a Windows service
                    // to read MainModule.FileName. Keep this optional so Xbox
                    // detection can fall back to the cached executable name.
                    string? executablePath = null;

                    try
                    {
                        executablePath =
                            process.MainModule
                                ?.FileName;
                    }
                    catch
                    {
                    }

                    foreach (InstalledGame game
                        in games)
                    {
                        bool executableMatches = false;

                        if (game.Launcher.Equals(
                                "Xbox",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            string normalizedInstallPath =
                                Path.GetFullPath(
                                    game.InstallPath);

                            // Best case: we can read the real process path and
                            // verify that it lives anywhere under this game's
                            // Content folder. This catches nested Shipping EXEs.
                            if (!string.IsNullOrWhiteSpace(
                                    executablePath))
                            {
                                string gameFolder =
                                    normalizedInstallPath
                                        .TrimEnd(
                                            Path.DirectorySeparatorChar,
                                            Path.AltDirectorySeparatorChar)
                                    +
                                    Path.DirectorySeparatorChar;

                                try
                                {
                                    executableMatches =
                                        Path.GetFullPath(
                                                executablePath)
                                            .StartsWith(
                                                gameFolder,
                                                StringComparison.OrdinalIgnoreCase);
                                }
                                catch
                                {
                                }
                            }

                            // Fallback for protected/packaged GDK processes:
                            // match ProcessName against every EXE discovered
                            // underneath this game's Content folder at refresh.
                            if (!executableMatches &&
                                xboxExecutableNamesByInstallPath.TryGetValue(
                                    normalizedInstallPath,
                                    out HashSet<string>? xboxExecutableNames))
                            {
                                executableMatches =
                                    xboxExecutableNames.Contains(
                                        processName);

                            }
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(
                                executablePath))
                            {
                                continue;
                            }

                            if (!string.IsNullOrWhiteSpace(
                                game.ExecutablePath))
                            {
                                executableMatches =
                                    Path.GetFullPath(
                                            executablePath)
                                        .Equals(
                                            Path.GetFullPath(
                                                game.ExecutablePath),
                                            StringComparison.OrdinalIgnoreCase);
                            }
                            else
                            {
                                string gameFolder =
                                    Path.GetFullPath(
                                            game.InstallPath)
                                        .TrimEnd(
                                            Path.DirectorySeparatorChar,
                                            Path.AltDirectorySeparatorChar)
                                    +
                                    Path.DirectorySeparatorChar;

                                executableMatches =
                                    executablePath.StartsWith(
                                        gameFolder,
                                        StringComparison.OrdinalIgnoreCase);
                            }
                        }

                        if (!executableMatches)
                        {
                            continue;
                        }

                        runningGames.Add(
                            game.Name);

                        break;
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            return runningGames;
        }

        // =========================================================
        // GAMES.TXT FALLBACK
        // =========================================================

        private static void LoadManualGames(
            HashSet<string> runningGames)
        {
            string gamesFile =
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Games.txt");

            if (!File.Exists(
                gamesFile))
            {
                return;
            }

            foreach (string line
                in File.ReadAllLines(
                    gamesFile))
            {
                if (string.IsNullOrWhiteSpace(
                    line))
                {
                    continue;
                }

                string[] parts =
                    line.Split('|');

                if (parts.Length != 2)
                {
                    continue;
                }

                string gameName =
                    parts[0].Trim();

                string processName =
                    parts[1].Trim();

                try
                {
                    Process[] matches =
                        Process.GetProcessesByName(
                            processName);

                    if (matches.Length > 0)
                    {
                        runningGames.Add(
                            gameName);
                    }

                    foreach (Process process
                        in matches)
                    {
                        process.Dispose();
                    }
                }
                catch
                {
                }
            }
        }

        // =========================================================
        // IGNORE LISTS
        // =========================================================

        private static void RemoveIgnoredGames(
            HashSet<string> runningGames)
        {
            runningGames.RemoveWhere(
                gameName =>
                    IgnoredGameNames.Contains(
                        gameName));
        }

        private static bool ShouldIgnoreProcess(
            string processName)
        {
            string[] ignoredProcesses =
            {
                // Steam
                "wallpaper32",
                "wallpaper64",
                "steam",
                "steamwebhelper",

                // Crash / helper programs
                "UnityCrashHandler32",
                "UnityCrashHandler64",
                "CrashReportClient",
                "crashpad_handler",

                // Anti-cheat
                "EasyAntiCheat",
                "EasyAntiCheat_EOS",

                // Epic
                "EpicGamesLauncher",
                "EpicWebHelper",
                "EOSOverlayRenderer-Win32-Shipping",
                "EOSOverlayRenderer-Win64-Shipping",

                // Riot
                "RiotClientServices",
                "RiotClientUx",
                "RiotClientUxRender",
                "vgc",
                "vgtray",

                // Battle.net
                "Battle.net",
                "Agent",
                "BlizzardError",
                "BlizzardBrowser",
                "BlizzardUpdateAgent",

                // EA
                "EADesktop",
                "EABackgroundService",
                "EALauncher",
                "EALocalHostSvc",
                "Origin",
                "OriginWebHelperService",

                // Ubisoft
                "UbisoftConnect",
                "upc",
                "UplayWebCore",
                "UplayService",

                // Minecraft launchers / helpers
                "MinecraftLauncher",
                "CurseForge",
                "Overwolf",
                "PrismLauncher",
                "Modrinth App",
                "ModrinthApp",
                "Theseus",
                "ATLauncher",
                "GDLauncher",
                "MultiMC",

                // Xbox / GDK helpers and installers
                "GamingServices",
                "GamingServicesNet",
                "GameBar",
                "GameBarFTServer",
                "XboxPcApp",
                "XboxAppServices",
                "MicrosoftGameUI",
                "BootstrapPackagedGame",

                // Installers
                "installscript",
                "vc_redist",
                "DXSETUP"
            };

            return ignoredProcesses.Contains(
                processName,
                StringComparer.OrdinalIgnoreCase);
        }

        // =========================================================
        // LIVE DASHBOARD STATE
        // =========================================================

        private void UpdateActiveSessionFile()
        {
            string activeSessionPath =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "GameWatch",
                    "ActiveSession.txt");

            try
            {
                if (activeGames.Count == 0)
                {
                    File.WriteAllText(
                        activeSessionPath,
                        string.Empty);

                    return;
                }

                KeyValuePair<string, ActiveGame> currentGame =
                    activeGames
                        .OrderBy(
                            game => game.Value.StartTime)
                        .First();

                string text =
                    $"{currentGame.Key}|{currentGame.Value.StartTime:o}";

                File.WriteAllText(
                    activeSessionPath,
                    text);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not update active session file.");
            }
        }

        private void ClearActiveSessionFile()
        {
            string activeSessionPath =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "GameWatch",
                    "ActiveSession.txt");

            try
            {
                File.WriteAllText(
                    activeSessionPath,
                    string.Empty);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not clear active session file.");
            }
        }

        // =========================================================
        // MODEL
        // =========================================================

        private record ActiveGame(
            DateTime StartTime,
            string Launcher);

        private record InstalledGame(
            string Name,
            string InstallPath,
            string Launcher,
            string? ExecutablePath = null);
    }
}