using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GameWatchDashboard
{
    public partial class MainWindow : Window
    {
        private readonly string databasePath;
        private readonly string activeSessionPath;
        private readonly string settingsPath;

        private readonly DispatcherTimer liveTimer;
        private readonly TaskbarIcon trayIcon;

        private DashboardSettings dashboardSettings;
        private bool gameWasRunning;
        private bool allowExit;
        private string? activeGameName;
        private DateTimeOffset? activeGameStartTime;

        private const string StartupRegistryPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";

        private const string StartupValueName =
            "GameWatchDashboard";

        private const string CurrentVersion =
            "1.1.0";

        private const string GitHubLatestReleaseApi =
            "https://api.github.com/repos/JoPrew-code/GameWatch/releases/latest";

        private static readonly HttpClient UpdateHttpClient =
            CreateUpdateHttpClient();

        private UpdateInfo? availableUpdate;

        public MainWindow()
        {
            InitializeComponent();

            trayIcon =
                InitializeTrayIcon();

            Closing +=
                MainWindow_Closing;

            string gameWatchFolder =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "GameWatch");

            databasePath =
                Path.Combine(
                    gameWatchFolder,
                    "GameWatch.db");

            activeSessionPath =
                Path.Combine(
                    gameWatchFolder,
                    "ActiveSession.txt");

            string settingsFolder =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "GameWatch");

            Directory.CreateDirectory(
                settingsFolder);

            settingsPath =
                Path.Combine(
                    settingsFolder,
                    "DashboardSettings.json");

            dashboardSettings =
                LoadDashboardSettings();

            SettingsDatabasePathText.Text =
                databasePath;

            SettingsActiveSessionPathText.Text =
                activeSessionPath;

            InitializeSettingsControls();

            // Sidebar
            DashboardButton.Click += DashboardButton_Click;
            LibraryButton.Click += LibraryButton_Click;
            SessionsButton.Click += SessionsButton_Click;
            StatisticsButton.Click += StatisticsButton_Click;
            CalendarButton.Click += CalendarButton_Click;
            AchievementsButton.Click += AchievementsButton_Click;
            SettingsButton.Click += SettingsButton_Click;

            LoadAllData();

            UpdateNowPlaying();

            liveTimer =
                new DispatcherTimer
                {
                    Interval =
                        TimeSpan.FromSeconds(1)
                };

            liveTimer.Tick +=
                LiveTimer_Tick;

            liveTimer.Start();

            Loaded +=
                async (_, _) =>
                {
                    await CheckForUpdatesAsync(
                        showMessages: false);
                };
        }

        // ============================================================
        // SYSTEM TRAY
        // ============================================================

        private TaskbarIcon InitializeTrayIcon()
        {
            TaskbarIcon icon =
                new()
                {
                    ToolTipText =
                        "GameWatch",

                    IconSource =
                        CreateTrayIconImageSource(),

                    Visibility =
                        Visibility.Visible
                };

            ContextMenu menu =
                new();

            MenuItem openItem =
                new()
                {
                    Header =
                        "Open GameWatch"
                };

            MenuItem exitItem =
                new()
                {
                    Header =
                        "Exit GameWatch"
                };

            openItem.Click +=
                (_, _) =>
                {
                    ShowFromTray();
                };

            exitItem.Click +=
                (_, _) =>
                {
                    ExitGameWatch();
                };

            menu.Items.Add(
                openItem);

            menu.Items.Add(
                new Separator());

            menu.Items.Add(
                exitItem);

            icon.ContextMenu =
                menu;

            icon.TrayMouseDoubleClick +=
                (_, _) =>
                {
                    ShowFromTray();
                };

            return icon;
        }

        private static ImageSource CreateTrayIconImageSource()
        {
            const int size =
                32;

            DrawingVisual visual =
                new();

            using (DrawingContext drawingContext =
                   visual.RenderOpen())
            {
                SolidColorBrush greenBrush =
                    new(
                        Color.FromRgb(
                            124,
                            255,
                            107));

                SolidColorBrush darkBrush =
                    new(
                        Color.FromRgb(
                            13,
                            15,
                            18));

                drawingContext.DrawEllipse(
                    greenBrush,
                    null,
                    new Point(16, 16),
                    15,
                    15);

                drawingContext.DrawEllipse(
                    darkBrush,
                    null,
                    new Point(16, 16),
                    8,
                    8);
            }

            RenderTargetBitmap bitmap =
                new(
                    size,
                    size,
                    96,
                    96,
                    PixelFormats.Pbgra32);

            bitmap.Render(
                visual);

            bitmap.Freeze();

            return bitmap;
        }

        private void MainWindow_Closing(
            object? sender,
            CancelEventArgs e)
        {
            if (allowExit)
            {
                return;
            }

            if (dashboardSettings.MinimizeToTray)
            {
                e.Cancel =
                    true;

                HideToTray();
                return;
            }

            allowExit =
                true;

            liveTimer.Stop();
            trayIcon.Dispose();
        }

        private void HideToTray()
        {
            ShowInTaskbar =
                false;

            Hide();
        }

        private void ShowFromTray()
        {
            Dispatcher.Invoke(
                () =>
                {
                    ShowInTaskbar =
                        true;

                    Show();

                    if (WindowState ==
                        WindowState.Minimized)
                    {
                        WindowState =
                            WindowState.Normal;
                    }

                    Activate();

                    Topmost =
                        true;

                    Topmost =
                        false;

                    Focus();
                });
        }

        private void ExitGameWatch()
        {
            Dispatcher.Invoke(
                () =>
                {
                    allowExit =
                        true;

                    liveTimer.Stop();

                    trayIcon.Dispose();

                    Application.Current.Shutdown();
                });
        }

        // ============================================================
        // SETTINGS
        // ============================================================

        private void InitializeSettingsControls()
        {
            SettingsLaunchAtStartupCheckBox.IsChecked =
                IsLaunchAtStartupEnabled();

            SettingsMinimizeToTrayCheckBox.IsChecked =
                dashboardSettings.MinimizeToTray;

            SettingsNotificationsCheckBox.IsChecked =
                dashboardSettings.NotificationsEnabled;

            SettingsCurrentVersionText.Text =
                CurrentVersion;

            SettingsLatestVersionText.Text =
                "Not checked yet";

            SettingsAboutVersionText.Text =
                $"Dashboard Version {CurrentVersion}";

            SettingsInstallUpdateButton.Visibility =
                Visibility.Collapsed;

            SettingsLaunchAtStartupCheckBox.Click +=
                SettingsLaunchAtStartupCheckBox_Click;

            SettingsMinimizeToTrayCheckBox.Click +=
                SettingsMinimizeToTrayCheckBox_Click;

            SettingsNotificationsCheckBox.Click +=
                SettingsNotificationsCheckBox_Click;

            SettingsOpenDatabaseFolderButton.Click +=
                SettingsOpenDatabaseFolderButton_Click;

            SettingsOpenLogsButton.Click +=
                SettingsOpenLogsButton_Click;

            SettingsCheckForUpdatesButton.Click +=
                SettingsCheckForUpdatesButton_Click;

            SettingsInstallUpdateButton.Click +=
                SettingsInstallUpdateButton_Click;
        }

        private DashboardSettings LoadDashboardSettings()
        {
            try
            {
                if (!File.Exists(settingsPath))
                {
                    return new DashboardSettings();
                }

                string json =
                    File.ReadAllText(settingsPath);

                DashboardSettings? loadedSettings =
                    JsonSerializer.Deserialize<DashboardSettings>(json);

                return loadedSettings ??
                       new DashboardSettings();
            }
            catch
            {
                return new DashboardSettings();
            }
        }

        private void SaveDashboardSettings()
        {
            try
            {
                string? folder =
                    Path.GetDirectoryName(settingsPath);

                if (!string.IsNullOrWhiteSpace(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string json =
                    JsonSerializer.Serialize(
                        dashboardSettings,
                        new JsonSerializerOptions
                        {
                            WriteIndented =
                                true
                        });

                File.WriteAllText(
                    settingsPath,
                    json);
            }
            catch (Exception ex)
            {
                SettingsStatusText.Text =
                    $"Could not save settings: {ex.Message}";
            }
        }

        private static bool IsLaunchAtStartupEnabled()
        {
            try
            {
                using RegistryKey? key =
                    Registry.CurrentUser.OpenSubKey(
                        StartupRegistryPath,
                        false);

                string? value =
                    key?.GetValue(StartupValueName)
                        as string;

                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }

        private static bool SetLaunchAtStartup(
            bool enabled)
        {
            try
            {
                using RegistryKey? key =
                    Registry.CurrentUser.CreateSubKey(
                        StartupRegistryPath);

                if (key == null)
                {
                    return false;
                }

                if (enabled)
                {
                    string? executablePath =
                        Environment.ProcessPath;

                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        return false;
                    }

                    key.SetValue(
                        StartupValueName,
                        $"\"{executablePath}\"");
                }
                else
                {
                    key.DeleteValue(
                        StartupValueName,
                        false);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SettingsLaunchAtStartupCheckBox_Click(
            object sender,
            RoutedEventArgs e)
        {
            bool enabled =
                SettingsLaunchAtStartupCheckBox.IsChecked ==
                true;

            if (SetLaunchAtStartup(enabled))
            {
                SettingsStatusText.Text =
                    enabled
                        ? "GameWatch will launch when you sign in to Windows."
                        : "GameWatch startup launch disabled.";
            }
            else
            {
                SettingsLaunchAtStartupCheckBox.IsChecked =
                    !enabled;

                SettingsStatusText.Text =
                    "Windows startup setting could not be changed.";
            }
        }

        private void SettingsMinimizeToTrayCheckBox_Click(
            object sender,
            RoutedEventArgs e)
        {
            dashboardSettings.MinimizeToTray =
                SettingsMinimizeToTrayCheckBox.IsChecked ==
                true;

            SaveDashboardSettings();

            SettingsStatusText.Text =
                dashboardSettings.MinimizeToTray
                    ? "Closing the dashboard will hide it in the system tray."
                    : "Closing the dashboard will exit the dashboard app.";
        }

        private void SettingsNotificationsCheckBox_Click(
            object sender,
            RoutedEventArgs e)
        {
            dashboardSettings.NotificationsEnabled =
                SettingsNotificationsCheckBox.IsChecked ==
                true;

            SaveDashboardSettings();

            SettingsStatusText.Text =
                dashboardSettings.NotificationsEnabled
                    ? "Desktop notifications enabled."
                    : "Desktop notifications disabled.";
        }

        private void SettingsOpenDatabaseFolderButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                string? folder =
                    Path.GetDirectoryName(databasePath);

                if (string.IsNullOrWhiteSpace(folder))
                {
                    return;
                }

                Directory.CreateDirectory(folder);

                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            folder,

                        UseShellExecute =
                            true
                    });

                SettingsStatusText.Text =
                    "Opened the GameWatch database folder.";
            }
            catch (Exception ex)
            {
                SettingsStatusText.Text =
                    $"Could not open database folder: {ex.Message}";
            }
        }

        private void SettingsOpenLogsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            "eventvwr.msc",

                        UseShellExecute =
                            true
                    });

                SettingsStatusText.Text =
                    "Opened Windows Event Viewer for service logs.";
            }
            catch (Exception ex)
            {
                SettingsStatusText.Text =
                    $"Could not open Event Viewer: {ex.Message}";
            }
        }

        // ============================================================
        // UPDATES
        // ============================================================

        private static HttpClient CreateUpdateHttpClient()
        {
            HttpClient client =
                new();

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"GameWatchDashboard/{CurrentVersion}");

            client.DefaultRequestHeaders.Accept.ParseAdd(
                "application/vnd.github+json");

            client.Timeout =
                TimeSpan.FromSeconds(30);

            return client;
        }

        private async void SettingsCheckForUpdatesButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await CheckForUpdatesAsync(
                showMessages: true);
        }

        private async void SettingsInstallUpdateButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            await DownloadAndInstallUpdateAsync();
        }

        private async Task CheckForUpdatesAsync(
            bool showMessages)
        {
            SettingsCheckForUpdatesButton.IsEnabled =
                false;

            SettingsInstallUpdateButton.IsEnabled =
                false;

            SettingsUpdateStatusText.Text =
                "Checking GitHub for the latest GameWatch release...";

            try
            {
                using HttpResponseMessage response =
                    await UpdateHttpClient.GetAsync(
                        GitHubLatestReleaseApi);

                response.EnsureSuccessStatusCode();

                string json =
                    await response.Content.ReadAsStringAsync();

                using JsonDocument document =
                    JsonDocument.Parse(json);

                JsonElement root =
                    document.RootElement;

                if (!root.TryGetProperty(
                        "tag_name",
                        out JsonElement tagElement))
                {
                    throw new InvalidOperationException(
                        "The latest GitHub release did not contain a version tag.");
                }

                string? tagName =
                    tagElement.GetString();

                string latestVersion =
                    NormalizeVersion(
                        tagName);

                SettingsLatestVersionText.Text =
                    latestVersion;

                availableUpdate =
                    null;

                SettingsInstallUpdateButton.Visibility =
                    Visibility.Collapsed;

                if (!IsNewerVersion(
                        latestVersion,
                        CurrentVersion))
                {
                    SettingsUpdateStatusText.Text =
                        $"You're up to date. GameWatch {CurrentVersion} is the latest version.";

                    if (showMessages)
                    {
                        MessageBox.Show(
                            this,
                            $"GameWatch {CurrentVersion} is already the latest version.",
                            "GameWatch Updates",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }

                    return;
                }

                if (!root.TryGetProperty(
                        "assets",
                        out JsonElement assetsElement) ||
                    assetsElement.ValueKind !=
                        JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        "The latest release does not contain downloadable assets.");
                }

                string? installerDownloadUrl =
                    null;

                foreach (JsonElement asset in
                    assetsElement.EnumerateArray())
                {
                    if (!asset.TryGetProperty(
                            "name",
                            out JsonElement nameElement) ||
                        !asset.TryGetProperty(
                            "browser_download_url",
                            out JsonElement urlElement))
                    {
                        continue;
                    }

                    string? assetName =
                        nameElement.GetString();

                    if (!string.Equals(
                            assetName,
                            "GameWatchSetup.exe",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    installerDownloadUrl =
                        urlElement.GetString();

                    break;
                }

                if (string.IsNullOrWhiteSpace(
                        installerDownloadUrl) ||
                    !IsTrustedGameWatchDownloadUrl(
                        installerDownloadUrl))
                {
                    throw new InvalidOperationException(
                        "A valid GameWatchSetup.exe asset was not found in the latest release.");
                }

                availableUpdate =
                    new UpdateInfo(
                        latestVersion,
                        installerDownloadUrl);

                SettingsUpdateStatusText.Text =
                    $"GameWatch {latestVersion} is available.";

                SettingsInstallUpdateButton.Content =
                    $"Download & Install {latestVersion}";

                SettingsInstallUpdateButton.Visibility =
                    Visibility.Visible;

                SettingsInstallUpdateButton.IsEnabled =
                    true;

                if (!showMessages &&
                    dashboardSettings.NotificationsEnabled)
                {
                    trayIcon.ShowBalloonTip(
                        "GameWatch Update Available",
                        $"Version {latestVersion} is ready to install.",
                        BalloonIcon.Info);
                }
            }
            catch (Exception ex)
            {
                SettingsLatestVersionText.Text =
                    "Unavailable";

                SettingsUpdateStatusText.Text =
                    $"Could not check for updates: {ex.Message}";

                if (showMessages)
                {
                    MessageBox.Show(
                        this,
                        $"GameWatch could not check for updates.\n\n{ex.Message}",
                        "GameWatch Updates",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                SettingsCheckForUpdatesButton.IsEnabled =
                    true;

                if (availableUpdate !=
                    null)
                {
                    SettingsInstallUpdateButton.IsEnabled =
                        true;
                }
            }
        }

        private async Task DownloadAndInstallUpdateAsync()
        {
            UpdateInfo? update =
                availableUpdate;

            if (update ==
                null)
            {
                await CheckForUpdatesAsync(
                    showMessages: true);

                update =
                    availableUpdate;

                if (update ==
                    null)
                {
                    return;
                }
            }

            MessageBoxResult answer =
                MessageBox.Show(
                    this,
                    $"Install GameWatch {update.Version}?\n\nThe installer will download from the official GameWatch GitHub release. GameWatch will close while the update installs. Your saved session database will be kept.",
                    "Install GameWatch Update",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information,
                    MessageBoxResult.Yes);

            if (answer !=
                MessageBoxResult.Yes)
            {
                return;
            }

            SettingsCheckForUpdatesButton.IsEnabled =
                false;

            SettingsInstallUpdateButton.IsEnabled =
                false;

            SettingsUpdateStatusText.Text =
                $"Downloading GameWatch {update.Version}...";

            try
            {
                string updateFolder =
                    Path.Combine(
                        Path.GetTempPath(),
                        "GameWatch",
                        "Updates",
                        update.Version);

                Directory.CreateDirectory(
                    updateFolder);

                string installerPath =
                    Path.Combine(
                        updateFolder,
                        "GameWatchSetup.exe");

                using HttpResponseMessage response =
                    await UpdateHttpClient.GetAsync(
                        update.DownloadUrl,
                        HttpCompletionOption.ResponseHeadersRead);

                response.EnsureSuccessStatusCode();

                await using Stream sourceStream =
                    await response.Content.ReadAsStreamAsync();

                await using FileStream destinationStream =
                    new(
                        installerPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);

                await sourceStream.CopyToAsync(
                    destinationStream);

                await destinationStream.FlushAsync();

                FileInfo installerFile =
                    new(
                        installerPath);

                if (!installerFile.Exists ||
                    installerFile.Length <
                        1024)
                {
                    throw new InvalidOperationException(
                        "The downloaded installer file was empty or incomplete.");
                }

                SettingsUpdateStatusText.Text =
                    "Download complete. Starting the GameWatch installer...";

                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName =
                            installerPath,

                        UseShellExecute =
                            true
                    });

                allowExit =
                    true;

                liveTimer.Stop();

                trayIcon.Dispose();

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                SettingsUpdateStatusText.Text =
                    $"Update failed: {ex.Message}";

                SettingsCheckForUpdatesButton.IsEnabled =
                    true;

                SettingsInstallUpdateButton.IsEnabled =
                    true;

                MessageBox.Show(
                    this,
                    $"GameWatch could not download or start the update.\n\n{ex.Message}",
                    "GameWatch Update Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static string NormalizeVersion(
            string? tagName)
        {
            if (string.IsNullOrWhiteSpace(
                tagName))
            {
                throw new InvalidOperationException(
                    "The release version was blank.");
            }

            string version =
                tagName.Trim();

            if (version.StartsWith(
                "v",
                StringComparison.OrdinalIgnoreCase))
            {
                version =
                    version[1..];
            }

            int prereleaseSeparator =
                version.IndexOf('-');

            if (prereleaseSeparator >=
                0)
            {
                version =
                    version[..prereleaseSeparator];
            }

            if (!Version.TryParse(
                version,
                out _))
            {
                throw new InvalidOperationException(
                    $"The release tag '{tagName}' is not a valid version number.");
            }

            return version;
        }

        private static bool IsNewerVersion(
            string latestVersion,
            string currentVersion)
        {
            if (!Version.TryParse(
                    latestVersion,
                    out Version? latest) ||
                !Version.TryParse(
                    currentVersion,
                    out Version? current))
            {
                return false;
            }

            return latest >
                   current;
        }

        private static bool IsTrustedGameWatchDownloadUrl(
            string downloadUrl)
        {
            if (!Uri.TryCreate(
                    downloadUrl,
                    UriKind.Absolute,
                    out Uri? uri))
            {
                return false;
            }

            if (!uri.Scheme.Equals(
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                !uri.Host.Equals(
                    "github.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string expectedPrefix =
                "/JoPrew-code/GameWatch/releases/download/";

            return uri.AbsolutePath.StartsWith(
                       expectedPrefix,
                       StringComparison.OrdinalIgnoreCase) &&
                   uri.AbsolutePath.EndsWith(
                       "/GameWatchSetup.exe",
                       StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        // NAVIGATION
        // ============================================================

        private void DashboardButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ShowPage(
                DashboardPage,
                DashboardButton);
        }

        private void LibraryButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            LoadAllData();

            ShowPage(
                LibraryPage,
                LibraryButton);
        }

        private void SessionsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            LoadAllData();

            ShowPage(
                SessionsPage,
                SessionsButton);
        }

        private void StatisticsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            LoadAllData();

            ShowPage(
                StatisticsPage,
                StatisticsButton);
        }

        private void CalendarButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            LoadAllData();

            ShowPage(
                CalendarPage,
                CalendarButton);
        }

        private void AchievementsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            LoadAllData();

            ShowPage(
                AchievementsPage,
                AchievementsButton);
        }

        private void SettingsButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            ShowPage(
                SettingsPage,
                SettingsButton);
        }

        private void ShowPage(
            UIElement page,
            Button selectedButton)
        {
            DashboardPage.Visibility =
                Visibility.Collapsed;

            LibraryPage.Visibility =
                Visibility.Collapsed;

            SessionsPage.Visibility =
                Visibility.Collapsed;

            StatisticsPage.Visibility =
                Visibility.Collapsed;

            CalendarPage.Visibility =
                Visibility.Collapsed;

            AchievementsPage.Visibility =
                Visibility.Collapsed;

            SettingsPage.Visibility =
                Visibility.Collapsed;

            page.Visibility =
                Visibility.Visible;

            SetSelectedButton(
                selectedButton);
        }

        private void SetSelectedButton(
            Button selectedButton)
        {
            Button[] buttons =
            {
                DashboardButton,
                LibraryButton,
                SessionsButton,
                StatisticsButton,
                CalendarButton,
                AchievementsButton,
                SettingsButton
            };

            foreach (Button button in buttons)
            {
                button.Background =
                    MakeBrush(
                        21,
                        25,
                        31);

                button.Foreground =
                    MakeBrush(
                        211,
                        215,
                        222);

                button.BorderBrush =
                    MakeBrush(
                        37,
                        42,
                        49);
            }

            selectedButton.Background =
                MakeBrush(
                    29,
                    38,
                    33);

            selectedButton.Foreground =
                MakeBrush(
                    124,
                    255,
                    107);

            selectedButton.BorderBrush =
                MakeBrush(
                    47,
                    59,
                    50);
        }

        // ============================================================
        // LIVE GAME
        // ============================================================

        private void LiveTimer_Tick(
            object? sender,
            EventArgs e)
        {
            UpdateNowPlaying();
        }

        private void UpdateNowPlaying()
        {
            try
            {
                if (!File.Exists(
                    activeSessionPath))
                {
                    HandleNoActiveGame();
                    return;
                }

                string text =
                    File.ReadAllText(
                        activeSessionPath)
                    .Trim();

                if (string.IsNullOrWhiteSpace(
                    text))
                {
                    HandleNoActiveGame();
                    return;
                }

                string[] parts =
                    text.Split(
                        '|',
                        2);

                if (parts.Length != 2)
                {
                    HandleNoActiveGame();
                    return;
                }

                string gameName =
                    parts[0].Trim();

                string startTimeText =
                    parts[1].Trim();

                if (!DateTimeOffset.TryParse(
                    startTimeText,
                    out DateTimeOffset startTime))
                {
                    HandleNoActiveGame();
                    return;
                }

                bool isNewTrackedGame =
                    !gameWasRunning ||
                    !string.Equals(
                        activeGameName,
                        gameName,
                        StringComparison.OrdinalIgnoreCase);

                if (gameWasRunning &&
                    isNewTrackedGame &&
                    !string.IsNullOrWhiteSpace(activeGameName))
                {
                    ShowSessionEndedNotification(
                        activeGameName,
                        activeGameStartTime);
                }

                gameWasRunning =
                    true;

                activeGameName =
                    gameName;

                activeGameStartTime =
                    startTime;

                if (isNewTrackedGame)
                {
                    ShowGameStartedNotification(
                        gameName);
                }

                NowPlayingGameText.Text =
                    gameName;

                NowPlayingSubtitleText.Text =
                    "Currently playing • GameWatch is tracking this session.";

                TimeSpan elapsed =
                    DateTimeOffset.Now -
                    startTime;

                if (elapsed < TimeSpan.Zero)
                {
                    elapsed =
                        TimeSpan.Zero;
                }

                int hours =
                    (int)elapsed.TotalHours;

                SessionTimerText.Text =
                    $"{hours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            }
            catch (IOException)
            {
                // Retry next second.
            }
            catch
            {
                HandleNoActiveGame();
            }
        }

        private void HandleNoActiveGame()
        {
            ShowNoGameDetected();

            if (gameWasRunning)
            {
                string? endedGameName =
                    activeGameName;

                DateTimeOffset? endedStartTime =
                    activeGameStartTime;

                gameWasRunning =
                    false;

                activeGameName =
                    null;

                activeGameStartTime =
                    null;

                LoadAllData();

                if (!string.IsNullOrWhiteSpace(endedGameName))
                {
                    ShowSessionEndedNotification(
                        endedGameName,
                        endedStartTime);
                }
            }
        }

        private void ShowGameStartedNotification(
            string gameName)
        {
            if (!dashboardSettings.NotificationsEnabled)
            {
                return;
            }

            trayIcon.ShowBalloonTip(
                "GameWatch",
                $"Now tracking {gameName}.",
                BalloonIcon.Info);
        }

        private void ShowSessionEndedNotification(
            string gameName,
            DateTimeOffset? startTime)
        {
            if (!dashboardSettings.NotificationsEnabled)
            {
                return;
            }

            string durationText =
                GetLatestSessionDurationText(
                    gameName) ??
                FormatElapsedDuration(
                    startTime);

            trayIcon.ShowBalloonTip(
                "GameWatch Session Complete",
                $"{gameName} • {durationText}",
                BalloonIcon.Info);
        }

        private string? GetLatestSessionDurationText(
            string gameName)
        {
            try
            {
                if (!File.Exists(databasePath))
                {
                    return null;
                }

                using SqliteConnection connection =
                    new(
                        $"Data Source={databasePath};Mode=ReadOnly");

                connection.Open();

                using SqliteCommand command =
                    connection.CreateCommand();

                command.CommandText =
                    """
                    SELECT DurationSeconds
                    FROM Sessions
                    WHERE GameName = $gameName
                    ORDER BY StartTime DESC
                    LIMIT 1;
                    """;

                command.Parameters.AddWithValue(
                    "$gameName",
                    gameName);

                object? result =
                    command.ExecuteScalar();

                if (result == null ||
                    result == DBNull.Value)
                {
                    return null;
                }

                int seconds =
                    Convert.ToInt32(result);

                return FormatNotificationDuration(
                    seconds);
            }
            catch
            {
                return null;
            }
        }

        private static string FormatElapsedDuration(
            DateTimeOffset? startTime)
        {
            if (startTime == null)
            {
                return "Session recorded";
            }

            TimeSpan elapsed =
                DateTimeOffset.Now -
                startTime.Value;

            if (elapsed < TimeSpan.Zero)
            {
                elapsed =
                    TimeSpan.Zero;
            }

            return FormatNotificationDuration(
                (int)elapsed.TotalSeconds);
        }

        private static string FormatNotificationDuration(
            int totalSeconds)
        {
            TimeSpan duration =
                TimeSpan.FromSeconds(
                    Math.Max(0, totalSeconds));

            int hours =
                (int)duration.TotalHours;

            if (hours > 0)
            {
                return $"{hours}h {duration.Minutes}m";
            }

            if (duration.Minutes > 0)
            {
                return $"{duration.Minutes}m {duration.Seconds}s";
            }

            return $"{duration.Seconds}s";
        }

        private void ShowNoGameDetected()
        {
            NowPlayingGameText.Text =
                "No game detected";

            NowPlayingSubtitleText.Text =
                "Start a game and GameWatch will detect it automatically.";

            SessionTimerText.Text =
                "00:00:00";
        }

        // ============================================================
        // LOAD DATA
        // ============================================================

        private void LoadAllData()
        {
            try
            {
                List<GameSession> sessions =
                    LoadSessions();

                UpdateDashboard(
                    sessions);

                UpdateLibrary(
                    sessions);

                UpdateSessionsPage(
                    sessions);

                UpdateStatistics(
                    sessions);

                UpdateCalendar(
                    sessions);

                UpdateAchievements(
                    sessions);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"GameWatch could not load dashboard data.\n\n{ex.Message}",
                    "GameWatch",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private List<GameSession> LoadSessions()
        {
            List<GameSession> sessions =
                new();

            if (!File.Exists(
                databasePath))
            {
                return sessions;
            }

            using SqliteConnection connection =
                new(
                    $"Data Source={databasePath};Mode=ReadOnly");

            connection.Open();

            const string sql =
                """
                SELECT
                    Id,
                    GameName,
                    Launcher,
                    StartTime,
                    EndTime,
                    DurationSeconds
                FROM Sessions
                ORDER BY StartTime DESC;
                """;

            using SqliteCommand command =
                new(
                    sql,
                    connection);

            using SqliteDataReader reader =
                command.ExecuteReader();

            while (reader.Read())
            {
                int id =
                    reader.GetInt32(0);

                string gameName =
                    reader.GetString(1);

                string launcher =
                    reader.IsDBNull(2)
                        ? "Unknown"
                        : reader.GetString(2);

                string startText =
                    reader.GetString(3);

                string endText =
                    reader.GetString(4);

                int durationSeconds =
                    reader.GetInt32(5);

                if (!DateTime.TryParse(
                    startText,
                    out DateTime startTime))
                {
                    continue;
                }

                if (!DateTime.TryParse(
                    endText,
                    out DateTime endTime))
                {
                    continue;
                }

                sessions.Add(
                    new GameSession(
                        id,
                        gameName,
                        launcher,
                        startTime,
                        endTime,
                        durationSeconds));
            }

            return sessions;
        }

        // ============================================================
        // DASHBOARD
        // ============================================================

        private void UpdateDashboard(
            List<GameSession> sessions)
        {
            DateTime now =
                DateTime.Now;

            DateTime today =
                now.Date;

            DateTime week =
                GetStartOfWeek(
                    now);

            DateTime month =
                new(
                    now.Year,
                    now.Month,
                    1);

            TodayPlaytimeText.Text =
                FormatDuration(
                    sessions
                        .Where(
                            s =>
                                s.StartTime >= today)
                        .Sum(
                            s =>
                                s.DurationSeconds));

            WeekPlaytimeText.Text =
                FormatDuration(
                    sessions
                        .Where(
                            s =>
                                s.StartTime >= week)
                        .Sum(
                            s =>
                                s.DurationSeconds));

            MonthPlaytimeText.Text =
                FormatDuration(
                    sessions
                        .Where(
                            s =>
                                s.StartTime >= month)
                        .Sum(
                            s =>
                                s.DurationSeconds));

            AllTimePlaytimeText.Text =
                FormatDuration(
                    sessions.Sum(
                        s =>
                            s.DurationSeconds));

            UpdateRecentSessions(
                sessions);

            UpdateTopGames(
                sessions);
        }

        private void UpdateRecentSessions(
            List<GameSession> sessions)
        {
            RecentSessionsPanel.Children.Clear();

            foreach (GameSession session in
                sessions
                    .OrderByDescending(
                        s =>
                            s.StartTime)
                    .Take(6))
            {
                RecentSessionsPanel.Children.Add(
                    CreateSessionRow(
                        session));
            }

            if (sessions.Count == 0)
            {
                RecentSessionsPanel.Children.Add(
                    CreateMutedText(
                        "No sessions recorded yet."));
            }
        }

        private void UpdateTopGames(
            List<GameSession> sessions)
        {
            TopGamesPanel.Children.Clear();

            var games =
                sessions
                    .GroupBy(
                        s =>
                            s.GameName)

                    .Select(
                        g =>
                            new
                            {
                                Name =
                                    g.Key,

                                Seconds =
                                    g.Sum(
                                        x =>
                                            x.DurationSeconds)
                            })

                    .OrderByDescending(
                        g =>
                            g.Seconds)

                    .Take(5)
                    .ToList();

            int rank =
                1;

            foreach (var game in games)
            {
                Grid row =
                    new()
                    {
                        Margin =
                            new Thickness(
                                0,
                                0,
                                0,
                                12)
                    };

                row.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            new GridLength(28)
                    });

                row.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            new GridLength(
                                1,
                                GridUnitType.Star)
                    });

                row.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            GridLength.Auto
                    });

                TextBlock number =
                    new()
                    {
                        Text =
                            rank.ToString(),

                        Foreground =
                            MakeBrush(
                                124,
                                255,
                                107),

                        FontWeight =
                            FontWeights.Bold
                    };

                TextBlock name =
                    new()
                    {
                        Text =
                            game.Name,

                        Foreground =
                            Brushes.White
                    };

                Grid.SetColumn(
                    name,
                    1);

                TextBlock duration =
                    new()
                    {
                        Text =
                            FormatDuration(
                                game.Seconds),

                        Foreground =
                            MakeBrush(
                                139,
                                148,
                                165)
                    };

                Grid.SetColumn(
                    duration,
                    2);

                row.Children.Add(number);
                row.Children.Add(name);
                row.Children.Add(duration);

                TopGamesPanel.Children.Add(row);

                rank++;
            }

            if (games.Count == 0)
            {
                TopGamesPanel.Children.Add(
                    CreateMutedText(
                        "No playtime data yet."));
            }
        }

        // ============================================================
        // LIBRARY
        // ============================================================

        private void UpdateLibrary(
            List<GameSession> sessions)
        {
            var games =
                sessions
                    .GroupBy(
                        s =>
                            s.GameName)

                    .Select(
                        g =>
                        {
                            GameSession? launcherSource =
                                g
                                    .Where(
                                        s =>
                                            !string.IsNullOrWhiteSpace(
                                                s.Launcher) &&
                                            !s.Launcher.Equals(
                                                "Unknown",
                                                StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(
                                        s =>
                                            s.StartTime)
                                    .FirstOrDefault();

                            string launcher =
                                launcherSource?.Launcher
                                ?? "Unknown";

                            return new GameLibraryItem(
                                g.Key,
                                launcher,
                                g.Count(),
                                g.Sum(
                                    x =>
                                        x.DurationSeconds),
                                g.Max(
                                    x =>
                                        x.StartTime));
                        })

                    .OrderByDescending(
                        g =>
                            g.LastPlayed)

                    .ToList();

            LibraryGameCountText.Text =
                games.Count.ToString();

            LibrarySessionCountText.Text =
                sessions.Count.ToString();

            LibraryPlaytimeText.Text =
                FormatDuration(
                    sessions.Sum(
                        s =>
                            s.DurationSeconds));

            LibraryGamesPanel.Children.Clear();

            foreach (GameLibraryItem game in games)
            {
                Border card =
                    new()
                    {
                        Background =
                            MakeBrush(
                                17,
                                20,
                                25),

                        BorderBrush =
                            MakeBrush(
                                37,
                                42,
                                49),

                        BorderThickness =
                            new Thickness(1),

                        CornerRadius =
                            new CornerRadius(10),

                        Padding =
                            new Thickness(16),

                        Margin =
                            new Thickness(
                                0,
                                0,
                                0,
                                10)
                    };

                Grid grid =
                    new();

                grid.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            new GridLength(
                                1,
                                GridUnitType.Star)
                    });

                grid.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            new GridLength(150)
                    });

                grid.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            new GridLength(110)
                    });

                StackPanel info =
                    new();

                info.Children.Add(
                    new TextBlock
                    {
                        Text =
                            game.GameName,

                        Foreground =
                            Brushes.White,

                        FontSize =
                            15,

                        FontWeight =
                            FontWeights.SemiBold
                    });

                StackPanel launcherLine =
                    new()
                    {
                        Orientation =
                            Orientation.Horizontal,

                        Margin =
                            new Thickness(
                                0,
                                4,
                                0,
                                0)
                    };

                Border launcherBadge =
                    CreateLauncherBadge(
                        game.Launcher);

                launcherLine.Children.Add(
                    launcherBadge);

                launcherLine.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"Last played {game.LastPlayed:MMM d, yyyy}",

                        Foreground =
                            MakeBrush(
                                139,
                                148,
                                165),

                        FontSize =
                            12,

                        VerticalAlignment =
                            VerticalAlignment.Center,

                        Margin =
                            new Thickness(
                                10,
                                0,
                                0,
                                0)
                    });

                info.Children.Add(
                    launcherLine);

                TextBlock playtime =
                    new()
                    {
                        Text =
                            FormatDuration(
                                game.TotalSeconds),

                        Foreground =
                            MakeBrush(
                                124,
                                255,
                                107),

                        VerticalAlignment =
                            VerticalAlignment.Center
                    };

                Grid.SetColumn(
                    playtime,
                    1);

                TextBlock count =
                    new()
                    {
                        Text =
                            $"{game.SessionCount} sessions",

                        Foreground =
                            MakeBrush(
                                139,
                                148,
                                165),

                        VerticalAlignment =
                            VerticalAlignment.Center
                    };

                Grid.SetColumn(
                    count,
                    2);

                grid.Children.Add(info);
                grid.Children.Add(playtime);
                grid.Children.Add(count);

                card.Child =
                    grid;

                LibraryGamesPanel.Children.Add(
                    card);
            }

            if (games.Count == 0)
            {
                LibraryGamesPanel.Children.Add(
                    CreateMutedText(
                        "No games recorded yet."));
            }
        }

        // ============================================================
        // SESSIONS PAGE
        // ============================================================

        private void UpdateSessionsPage(
            List<GameSession> sessions)
        {
            SessionsTotalText.Text =
                $"{sessions.Count} sessions";

            AllSessionsPanel.Children.Clear();

            foreach (GameSession session in
                sessions.OrderByDescending(
                    s =>
                        s.StartTime))
            {
                AllSessionsPanel.Children.Add(
                    CreateSessionRow(
                        session,
                        includeActions: true));
            }

            if (sessions.Count == 0)
            {
                AllSessionsPanel.Children.Add(
                    CreateMutedText(
                        "No sessions recorded yet."));
            }
        }

        // ============================================================
        // STATISTICS
        // ============================================================

        private void UpdateStatistics(
            List<GameSession> sessions)
        {
            int totalSeconds =
                sessions.Sum(
                    s => s.DurationSeconds);

            int averageSeconds =
                sessions.Count > 0
                    ? (int)sessions.Average(
                        s => s.DurationSeconds)
                    : 0;

            int gameCount =
                sessions
                    .Select(s => s.GameName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

            StatsTotalPlaytimeText.Text =
                FormatDuration(totalSeconds);

            StatsSessionCountText.Text =
                sessions.Count.ToString();

            StatsAverageSessionText.Text =
                FormatDuration(averageSeconds);

            StatsGamesPlayedText.Text =
                gameCount.ToString();

            var gamesByPlaytime =
                sessions
                    .GroupBy(
                        s => s.GameName,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(
                        g => new
                        {
                            Name = g.Key,
                            Seconds = g.Sum(s => s.DurationSeconds),
                            Sessions = g.Count()
                        })
                    .OrderByDescending(g => g.Seconds)
                    .ToList();

            var favorite =
                gamesByPlaytime.FirstOrDefault();

            if (favorite == null)
            {
                StatsMostPlayedGameText.Text =
                    "No data yet";

                StatsMostPlayedDurationText.Text =
                    "0h 00m";
            }
            else
            {
                StatsMostPlayedGameText.Text =
                    favorite.Name;

                StatsMostPlayedDurationText.Text =
                    FormatDuration(favorite.Seconds);
            }

            GameSession? longestSession =
                sessions
                    .OrderByDescending(
                        s => s.DurationSeconds)
                    .FirstOrDefault();

            if (longestSession == null)
            {
                StatsLongestSessionText.Text =
                    "0h 00m";

                StatsLongestSessionGameText.Text =
                    "No data yet";
            }
            else
            {
                StatsLongestSessionText.Text =
                    FormatDuration(
                        longestSession.DurationSeconds);

                StatsLongestSessionGameText.Text =
                    longestSession.GameName;
            }

            var launcherGroups =
                sessions
                    .Where(
                        s =>
                            !string.IsNullOrWhiteSpace(s.Launcher) &&
                            !s.Launcher.Equals(
                                "Unknown",
                                StringComparison.OrdinalIgnoreCase))
                    .GroupBy(
                        s => s.Launcher,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(
                        g => new
                        {
                            Name = g.Key,
                            Seconds = g.Sum(s => s.DurationSeconds),
                            Sessions = g.Count()
                        })
                    .OrderByDescending(g => g.Seconds)
                    .ToList();

            var mostUsedLauncher =
                launcherGroups.FirstOrDefault();

            if (mostUsedLauncher == null)
            {
                StatsMostUsedLauncherText.Text =
                    "No data yet";

                StatsMostUsedLauncherDurationText.Text =
                    "0h 00m";
            }
            else
            {
                StatsMostUsedLauncherText.Text =
                    mostUsedLauncher.Name;

                StatsMostUsedLauncherDurationText.Text =
                    FormatDuration(
                        mostUsedLauncher.Seconds);
            }

            if (sessions.Count == 0)
            {
                StatsAverageDailyText.Text =
                    "0h 00m";
            }
            else
            {
                DateTime firstDay =
                    sessions.Min(
                        s => s.StartTime.Date);

                DateTime today =
                    DateTime.Now.Date;

                int trackedCalendarDays =
                    Math.Max(
                        1,
                        (today - firstDay).Days + 1);

                int averageDailySeconds =
                    totalSeconds / trackedCalendarDays;

                StatsAverageDailyText.Text =
                    FormatDuration(
                        averageDailySeconds);
            }

            UpdatePlaytimeByGame(
                gamesByPlaytime
                    .Select(
                        g =>
                            new StatBreakdownItem(
                                g.Name,
                                g.Seconds,
                                g.Sessions))
                    .ToList());

            UpdatePlaytimeByLauncher(
                launcherGroups
                    .Select(
                        g =>
                            new StatBreakdownItem(
                                g.Name,
                                g.Seconds,
                                g.Sessions))
                    .ToList());

            UpdateLast7Days(
                sessions);
        }

        private void UpdatePlaytimeByGame(
            List<StatBreakdownItem> items)
        {
            StatsPlaytimeByGamePanel.Children.Clear();

            if (items.Count == 0)
            {
                StatsPlaytimeByGamePanel.Children.Add(
                    CreateMutedText(
                        "No game playtime recorded yet."));

                return;
            }

            int maximum =
                Math.Max(
                    1,
                    items.Max(
                        item => item.Seconds));

            foreach (StatBreakdownItem item in items)
            {
                StatsPlaytimeByGamePanel.Children.Add(
                    CreateStatBarRow(
                        item.Name,
                        item.Seconds,
                        maximum,
                        $"{item.SessionCount} session{(item.SessionCount == 1 ? "" : "s")}"));
            }
        }

        private void UpdatePlaytimeByLauncher(
            List<StatBreakdownItem> items)
        {
            StatsPlaytimeByLauncherPanel.Children.Clear();

            if (items.Count == 0)
            {
                StatsPlaytimeByLauncherPanel.Children.Add(
                    CreateMutedText(
                        "No launcher data recorded yet."));

                return;
            }

            int maximum =
                Math.Max(
                    1,
                    items.Max(
                        item => item.Seconds));

            foreach (StatBreakdownItem item in items)
            {
                StatsPlaytimeByLauncherPanel.Children.Add(
                    CreateStatBarRow(
                        item.Name,
                        item.Seconds,
                        maximum,
                        $"{item.SessionCount} session{(item.SessionCount == 1 ? "" : "s")}"));
            }
        }

        private void UpdateLast7Days(
            List<GameSession> sessions)
        {
            StatsLast7DaysPanel.Children.Clear();

            DateTime today =
                DateTime.Now.Date;

            List<DailyStatItem> days =
                new();

            for (int offset = 6; offset >= 0; offset--)
            {
                DateTime date =
                    today.AddDays(-offset);

                List<GameSession> daySessions =
                    sessions
                        .Where(
                            s => s.StartTime.Date == date)
                        .ToList();

                days.Add(
                    new DailyStatItem(
                        date,
                        daySessions.Sum(
                            s => s.DurationSeconds),
                        daySessions.Count));
            }

            int maximum =
                Math.Max(
                    1,
                    days.Max(
                        day => day.Seconds));

            foreach (DailyStatItem day in days)
            {
                string dayName =
                    day.Date == today
                        ? "Today"
                        : day.Date.ToString("ddd, MMM d");

                StatsLast7DaysPanel.Children.Add(
                    CreateStatBarRow(
                        dayName,
                        day.Seconds,
                        maximum,
                        $"{day.SessionCount} session{(day.SessionCount == 1 ? "" : "s")}"));
            }
        }

        private Border CreateStatBarRow(
            string label,
            int seconds,
            int maximumSeconds,
            string detail)
        {
            Border row =
                new()
                {
                    Margin =
                        new Thickness(
                            0,
                            0,
                            0,
                            14)
                };

            StackPanel stack =
                new();

            Grid header =
                new();

            header.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            header.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        GridLength.Auto
                });

            TextBlock labelText =
                new()
                {
                    Text =
                        label,

                    Foreground =
                        Brushes.White,

                    FontWeight =
                        FontWeights.SemiBold,

                    TextTrimming =
                        TextTrimming.CharacterEllipsis
                };

            TextBlock durationText =
                new()
                {
                    Text =
                        FormatDuration(seconds),

                    Foreground =
                        MakeBrush(
                            124,
                            255,
                            107),

                    FontWeight =
                        FontWeights.SemiBold
                };

            Grid.SetColumn(
                durationText,
                1);

            header.Children.Add(
                labelText);

            header.Children.Add(
                durationText);

            stack.Children.Add(
                header);

            stack.Children.Add(
                new TextBlock
                {
                    Text =
                        detail,

                    Foreground =
                        MakeBrush(
                            139,
                            148,
                            165),

                    FontSize =
                        11,

                    Margin =
                        new Thickness(
                            0,
                            3,
                            0,
                            6)
                });

            Grid barTrack =
                new()
                {
                    Height =
                        7,

                    Background =
                        MakeBrush(
                            37,
                            42,
                            49),

                    HorizontalAlignment =
                        HorizontalAlignment.Stretch
                };

            Border bar =
                new()
                {
                    Height =
                        7,

                    Background =
                        MakeBrush(
                            124,
                            255,
                            107),

                    HorizontalAlignment =
                        HorizontalAlignment.Left,

                    CornerRadius =
                        new CornerRadius(4)
                };

            double ratio =
                maximumSeconds <= 0
                    ? 0
                    : (double)seconds /
                      maximumSeconds;

            barTrack.SizeChanged +=
                (_, _) =>
                {
                    bar.Width =
                        Math.Max(
                            seconds > 0 ? 4 : 0,
                            barTrack.ActualWidth * ratio);
                };

            barTrack.Children.Add(
                bar);

            stack.Children.Add(
                barTrack);

            row.Child =
                stack;

            return row;
        }

        // ============================================================
        // CALENDAR
        // ============================================================

        private void UpdateCalendar(
            List<GameSession> sessions)
        {
            CalendarActivityPanel.Children.Clear();

            var days =
                sessions
                    .GroupBy(
                        s =>
                            s.StartTime.Date)

                    .OrderByDescending(
                        g =>
                            g.Key)

                    .ToList();

            foreach (var day in days)
            {
                Border dayCard =
                    new()
                    {
                        Background =
                            MakeBrush(
                                17,
                                20,
                                25),

                        BorderBrush =
                            MakeBrush(
                                37,
                                42,
                                49),

                        BorderThickness =
                            new Thickness(1),

                        CornerRadius =
                            new CornerRadius(10),

                        Padding =
                            new Thickness(16),

                        Margin =
                            new Thickness(
                                0,
                                0,
                                0,
                                12)
                    };

                StackPanel stack =
                    new();

                int seconds =
                    day.Sum(
                        s =>
                            s.DurationSeconds);

                stack.Children.Add(
                    new TextBlock
                    {
                        Text =
                            day.Key.ToString(
                                "dddd, MMMM d, yyyy"),

                        Foreground =
                            Brushes.White,

                        FontSize =
                            16,

                        FontWeight =
                            FontWeights.Bold
                    });

                stack.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"{day.Count()} sessions • {FormatDuration(seconds)}",

                        Foreground =
                            MakeBrush(
                                124,
                                255,
                                107),

                        Margin =
                            new Thickness(
                                0,
                                4,
                                0,
                                10)
                    });

                foreach (GameSession session in
                    day.OrderBy(
                        s =>
                            s.StartTime))
                {
                    stack.Children.Add(
                        new TextBlock
                        {
                            Text =
                                $"{session.StartTime:h:mm tt}  •  {session.GameName}  •  {session.Launcher}  •  {FormatDuration(session.DurationSeconds)}",

                            Foreground =
                                MakeBrush(
                                    211,
                                    215,
                                    222),

                            Margin =
                                new Thickness(
                                    0,
                                    3,
                                    0,
                                    3)
                        });
                }

                dayCard.Child =
                    stack;

                CalendarActivityPanel.Children.Add(
                    dayCard);
            }

            if (days.Count == 0)
            {
                CalendarActivityPanel.Children.Add(
                    CreateMutedText(
                        "No activity recorded yet."));
            }
        }

        // ============================================================
        // ACHIEVEMENTS
        // ============================================================

        private void UpdateAchievements(
            List<GameSession> sessions)
        {
            AchievementsPanel.Children.Clear();

            int totalSeconds =
                sessions.Sum(
                    s =>
                        s.DurationSeconds);

            int gameCount =
                sessions
                    .Select(
                        s =>
                            s.GameName)
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .Count();

            int longestSession =
                sessions.Count == 0
                    ? 0
                    : sessions.Max(
                        s =>
                            s.DurationSeconds);

            AddAchievement(
                "First Session",
                "Complete your first tracked gaming session.",
                sessions.Count >= 1);

            AddAchievement(
                "Getting Started",
                "Record 5 gaming sessions.",
                sessions.Count >= 5);

            AddAchievement(
                "Regular Gamer",
                "Record 25 gaming sessions.",
                sessions.Count >= 25);

            AddAchievement(
                "Explorer",
                "Play 3 different games.",
                gameCount >= 3);

            AddAchievement(
                "Game Collector",
                "Play 5 different games.",
                gameCount >= 5);

            AddAchievement(
                "One Hour Club",
                "Accumulate at least 1 hour of tracked playtime.",
                totalSeconds >= 3600);

            AddAchievement(
                "Ten Hour Club",
                "Accumulate at least 10 hours of tracked playtime.",
                totalSeconds >= 36000);

            AddAchievement(
                "Long Session",
                "Complete a gaming session lasting at least 2 hours.",
                longestSession >= 7200);
        }

        private void AddAchievement(
            string title,
            string description,
            bool unlocked)
        {
            Border card =
                new()
                {
                    Background =
                        MakeBrush(
                            17,
                            20,
                            25),

                    BorderBrush =
                        unlocked
                            ? MakeBrush(
                                47,
                                59,
                                50)
                            : MakeBrush(
                                37,
                                42,
                                49),

                    BorderThickness =
                        new Thickness(1),

                    CornerRadius =
                        new CornerRadius(10),

                    Padding =
                        new Thickness(16),

                    Margin =
                        new Thickness(
                            0,
                            0,
                            0,
                            10)
                };

            Grid grid =
                new();

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        GridLength.Auto
                });

            StackPanel info =
                new();

            info.Children.Add(
                new TextBlock
                {
                    Text =
                        title,

                    Foreground =
                        Brushes.White,

                    FontSize =
                        15,

                    FontWeight =
                        FontWeights.SemiBold
                });

            info.Children.Add(
                new TextBlock
                {
                    Text =
                        description,

                    Foreground =
                        MakeBrush(
                            139,
                            148,
                            165),

                    FontSize =
                        12,

                    Margin =
                        new Thickness(
                            0,
                            4,
                            0,
                            0)
                });

            TextBlock status =
                new()
                {
                    Text =
                        unlocked
                            ? "UNLOCKED"
                            : "LOCKED",

                    Foreground =
                        unlocked
                            ? MakeBrush(
                                124,
                                255,
                                107)
                            : MakeBrush(
                                105,
                                115,
                                134),

                    FontWeight =
                        FontWeights.Bold,

                    VerticalAlignment =
                        VerticalAlignment.Center
                };

            Grid.SetColumn(
                status,
                1);

            grid.Children.Add(info);
            grid.Children.Add(status);

            card.Child =
                grid;

            AchievementsPanel.Children.Add(
                card);
        }

        // ============================================================
        // SHARED UI
        // ============================================================

        private Border CreateSessionRow(
            GameSession session,
            bool includeActions = false)
        {
            Border row =
                new()
                {
                    Background =
                        MakeBrush(
                            17,
                            20,
                            25),

                    CornerRadius =
                        new CornerRadius(8),

                    Padding =
                        new Thickness(12),

                    Margin =
                        new Thickness(
                            0,
                            0,
                            0,
                            8)
                };

            Grid grid =
                new();

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        GridLength.Auto
                });

            StackPanel info =
                new();

            info.Children.Add(
                new TextBlock
                {
                    Text =
                        session.GameName,

                    Foreground =
                        Brushes.White,

                    FontWeight =
                        FontWeights.SemiBold,

                    FontSize =
                        14
                });

            StackPanel detailLine =
                new()
                {
                    Orientation =
                        Orientation.Horizontal,

                    Margin =
                        new Thickness(
                            0,
                            5,
                            0,
                            0)
                };

            detailLine.Children.Add(
                CreateLauncherBadge(
                    session.Launcher));

            detailLine.Children.Add(
                new TextBlock
                {
                    Text =
                        $"{session.StartTime:MMM d, yyyy h:mm tt} – {session.EndTime:h:mm tt}",

                    Foreground =
                        MakeBrush(
                            139,
                            148,
                            165),

                    FontSize =
                        12,

                    VerticalAlignment =
                        VerticalAlignment.Center,

                    Margin =
                        new Thickness(
                            10,
                            0,
                            0,
                            0)
                });

            info.Children.Add(
                detailLine);

            StackPanel rightSide =
                new()
                {
                    VerticalAlignment =
                        VerticalAlignment.Center,

                    HorizontalAlignment =
                        HorizontalAlignment.Right
                };

            TextBlock duration =
                new()
                {
                    Text =
                        FormatDuration(
                            session.DurationSeconds),

                    Foreground =
                        MakeBrush(
                            124,
                            255,
                            107),

                    FontWeight =
                        FontWeights.SemiBold,

                    HorizontalAlignment =
                        HorizontalAlignment.Right
                };

            rightSide.Children.Add(
                duration);

            if (includeActions)
            {
                StackPanel actionPanel =
                    new()
                    {
                        Orientation =
                            Orientation.Horizontal,

                        HorizontalAlignment =
                            HorizontalAlignment.Right,

                        Margin =
                            new Thickness(
                                0,
                                8,
                                0,
                                0)
                    };

                Button editButton =
                    CreateSessionActionButton(
                        "Edit",
                        false);

                editButton.Click +=
                    (_, _) =>
                    {
                        EditSession(
                            session);
                    };

                Button deleteButton =
                    CreateSessionActionButton(
                        "Delete",
                        true);

                deleteButton.Margin =
                    new Thickness(
                        8,
                        0,
                        0,
                        0);

                deleteButton.Click +=
                    (_, _) =>
                    {
                        DeleteSession(
                            session);
                    };

                actionPanel.Children.Add(
                    editButton);

                actionPanel.Children.Add(
                    deleteButton);

                rightSide.Children.Add(
                    actionPanel);
            }

            Grid.SetColumn(
                rightSide,
                1);

            grid.Children.Add(
                info);

            grid.Children.Add(
                rightSide);

            row.Child =
                grid;

            return row;
        }

        private static Button CreateSessionActionButton(
            string text,
            bool destructive)
        {
            return new Button
            {
                Content =
                    text,

                MinWidth =
                    68,

                Height =
                    30,

                Padding =
                    new Thickness(
                        12,
                        0,
                        12,
                        0),

                Background =
                    destructive
                        ? MakeBrush(
                            52,
                            24,
                            28)
                        : MakeBrush(
                            29,
                            38,
                            33),

                Foreground =
                    destructive
                        ? MakeBrush(
                            255,
                            135,
                            135)
                        : MakeBrush(
                            124,
                            255,
                            107),

                BorderBrush =
                    destructive
                        ? MakeBrush(
                            94,
                            43,
                            49)
                        : MakeBrush(
                            47,
                            59,
                            50),

                BorderThickness =
                    new Thickness(1),

                FontSize =
                    12,

                FontWeight =
                    FontWeights.SemiBold,

                Cursor =
                    System.Windows.Input.Cursors.Hand
            };
        }

        private void EditSession(
            GameSession session)
        {
            Window dialog =
                new()
                {
                    Title =
                        "Edit GameWatch Session",

                    Width =
                        480,

                    Height =
                        520,

                    MinWidth =
                        440,

                    MinHeight =
                        480,

                    WindowStartupLocation =
                        WindowStartupLocation.CenterOwner,

                    Owner =
                        this,

                    ResizeMode =
                        ResizeMode.NoResize,

                    Background =
                        MakeBrush(
                            13,
                            15,
                            18),

                    Foreground =
                        Brushes.White,

                    ShowInTaskbar =
                        false
                };

            Grid root =
                new()
                {
                    Margin =
                        new Thickness(24)
                };

            root.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        GridLength.Auto
                });

            root.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            root.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        GridLength.Auto
                });

            StackPanel heading =
                new();

            heading.Children.Add(
                new TextBlock
                {
                    Text =
                        "Edit Session",

                    Foreground =
                        Brushes.White,

                    FontSize =
                        24,

                    FontWeight =
                        FontWeights.Bold
                });

            heading.Children.Add(
                new TextBlock
                {
                    Text =
                        "Change the saved details below. Duration is recalculated automatically.",

                    Foreground =
                        MakeBrush(
                            139,
                            148,
                            165),

                    FontSize =
                        12,

                    TextWrapping =
                        TextWrapping.Wrap,

                    Margin =
                        new Thickness(
                            0,
                            5,
                            0,
                            18)
                });

            Grid.SetRow(
                heading,
                0);

            root.Children.Add(
                heading);

            StackPanel fields =
                new();

            TextBox gameNameBox =
                CreateSessionEditTextBox(
                    session.GameName);

            TextBox launcherBox =
                CreateSessionEditTextBox(
                    session.Launcher);

            TextBox startTimeBox =
                CreateSessionEditTextBox(
                    session.StartTime.ToString(
                        "yyyy-MM-dd HH:mm:ss"));

            TextBox endTimeBox =
                CreateSessionEditTextBox(
                    session.EndTime.ToString(
                        "yyyy-MM-dd HH:mm:ss"));

            AddSessionEditField(
                fields,
                "GAME NAME",
                gameNameBox,
                null);

            AddSessionEditField(
                fields,
                "LAUNCHER",
                launcherBox,
                null);

            AddSessionEditField(
                fields,
                "START TIME",
                startTimeBox,
                "Format: YYYY-MM-DD HH:MM:SS");

            AddSessionEditField(
                fields,
                "END TIME",
                endTimeBox,
                "Format: YYYY-MM-DD HH:MM:SS");

            Grid.SetRow(
                fields,
                1);

            root.Children.Add(
                fields);

            StackPanel buttons =
                new()
                {
                    Orientation =
                        Orientation.Horizontal,

                    HorizontalAlignment =
                        HorizontalAlignment.Right,

                    Margin =
                        new Thickness(
                            0,
                            18,
                            0,
                            0)
                };

            Button cancelButton =
                new()
                {
                    Content =
                        "Cancel",

                    MinWidth =
                        90,

                    Height =
                        34,

                    Background =
                        MakeBrush(
                            21,
                            25,
                            31),

                    Foreground =
                        MakeBrush(
                            211,
                            215,
                            222),

                    BorderBrush =
                        MakeBrush(
                            37,
                            42,
                            49),

                    BorderThickness =
                        new Thickness(1),

                    Cursor =
                        System.Windows.Input.Cursors.Hand
                };

            Button saveButton =
                new()
                {
                    Content =
                        "Save Changes",

                    MinWidth =
                        120,

                    Height =
                        34,

                    Margin =
                        new Thickness(
                            10,
                            0,
                            0,
                            0),

                    Background =
                        MakeBrush(
                            29,
                            38,
                            33),

                    Foreground =
                        MakeBrush(
                            124,
                            255,
                            107),

                    BorderBrush =
                        MakeBrush(
                            47,
                            59,
                            50),

                    BorderThickness =
                        new Thickness(1),

                    FontWeight =
                        FontWeights.SemiBold,

                    Cursor =
                        System.Windows.Input.Cursors.Hand
                };

            cancelButton.Click +=
                (_, _) =>
                {
                    dialog.DialogResult =
                        false;
                };

            saveButton.Click +=
                (_, _) =>
                {
                    string gameName =
                        gameNameBox.Text.Trim();

                    string launcher =
                        launcherBox.Text.Trim();

                    if (string.IsNullOrWhiteSpace(
                        gameName))
                    {
                        MessageBox.Show(
                            dialog,
                            "Game name cannot be blank.",
                            "GameWatch",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        return;
                    }

                    if (string.IsNullOrWhiteSpace(
                        launcher))
                    {
                        launcher =
                            "Unknown";
                    }

                    if (!DateTime.TryParse(
                        startTimeBox.Text.Trim(),
                        out DateTime startTime))
                    {
                        MessageBox.Show(
                            dialog,
                            "Start time is not valid. Use a value like 2026-09-04 17:30:00.",
                            "GameWatch",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        return;
                    }

                    if (!DateTime.TryParse(
                        endTimeBox.Text.Trim(),
                        out DateTime endTime))
                    {
                        MessageBox.Show(
                            dialog,
                            "End time is not valid. Use a value like 2026-09-04 18:45:00.",
                            "GameWatch",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        return;
                    }

                    if (endTime <
                        startTime)
                    {
                        MessageBox.Show(
                            dialog,
                            "End time cannot be earlier than start time.",
                            "GameWatch",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        return;
                    }

                    int durationSeconds =
                        (int)Math.Min(
                            int.MaxValue,
                            Math.Max(
                                0,
                                (endTime - startTime).TotalSeconds));

                    if (!UpdateSessionInDatabase(
                        session.Id,
                        gameName,
                        launcher,
                        startTime,
                        endTime,
                        durationSeconds))
                    {
                        return;
                    }

                    dialog.DialogResult =
                        true;
                };

            buttons.Children.Add(
                cancelButton);

            buttons.Children.Add(
                saveButton);

            Grid.SetRow(
                buttons,
                2);

            root.Children.Add(
                buttons);

            dialog.Content =
                root;

            bool? result =
                dialog.ShowDialog();

            if (result ==
                true)
            {
                LoadAllData();
            }
        }

        private static TextBox CreateSessionEditTextBox(
            string text)
        {
            return new TextBox
            {
                Text =
                    text,

                Height =
                    34,

                Padding =
                    new Thickness(
                        9,
                        5,
                        9,
                        5),

                Background =
                    MakeBrush(
                        17,
                        20,
                        25),

                Foreground =
                    Brushes.White,

                BorderBrush =
                    MakeBrush(
                        37,
                        42,
                        49),

                BorderThickness =
                    new Thickness(1),

                CaretBrush =
                    Brushes.White,

                FontSize =
                    13
            };
        }

        private static void AddSessionEditField(
            Panel panel,
            string label,
            Control control,
            string? helpText)
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text =
                        label,

                    Foreground =
                        MakeBrush(
                            124,
                            133,
                            151),

                    FontSize =
                        10,

                    FontWeight =
                        FontWeights.SemiBold,

                    Margin =
                        new Thickness(
                            0,
                            0,
                            0,
                            5)
                });

            panel.Children.Add(
                control);

            if (!string.IsNullOrWhiteSpace(
                helpText))
            {
                panel.Children.Add(
                    new TextBlock
                    {
                        Text =
                            helpText,

                        Foreground =
                            MakeBrush(
                                105,
                                115,
                                134),

                        FontSize =
                            10,

                        Margin =
                            new Thickness(
                                0,
                                4,
                                0,
                                0)
                    });
            }

            control.Margin =
                new Thickness(
                    0,
                    0,
                    0,
                    14);
        }

        private bool UpdateSessionInDatabase(
            int sessionId,
            string gameName,
            string launcher,
            DateTime startTime,
            DateTime endTime,
            int durationSeconds)
        {
            try
            {
                using SqliteConnection connection =
                    new(
                        $"Data Source={databasePath}");

                connection.Open();

                using SqliteCommand command =
                    connection.CreateCommand();

                command.CommandText =
                    """
                    UPDATE Sessions
                    SET
                        GameName = $gameName,
                        Launcher = $launcher,
                        StartTime = $startTime,
                        EndTime = $endTime,
                        DurationSeconds = $durationSeconds
                    WHERE Id = $id;
                    """;

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
                    durationSeconds);

                command.Parameters.AddWithValue(
                    "$id",
                    sessionId);

                int affectedRows =
                    command.ExecuteNonQuery();

                if (affectedRows ==
                    0)
                {
                    MessageBox.Show(
                        this,
                        "That session could not be found. Refresh GameWatch and try again.",
                        "GameWatch",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"GameWatch could not update that session.\n\n{ex.Message}",
                    "GameWatch",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                return false;
            }
        }

        private void DeleteSession(
            GameSession session)
        {
            MessageBoxResult answer =
                MessageBox.Show(
                    this,
                    $"Delete this {session.GameName} session?\n\n{session.StartTime:MMM d, yyyy h:mm tt} – {session.EndTime:h:mm tt}\n{FormatDuration(session.DurationSeconds)}\n\nThis cannot be undone.",
                    "Delete GameWatch Session",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

            if (answer !=
                MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                using SqliteConnection connection =
                    new(
                        $"Data Source={databasePath}");

                connection.Open();

                using SqliteCommand command =
                    connection.CreateCommand();

                command.CommandText =
                    "DELETE FROM Sessions WHERE Id = $id;";

                command.Parameters.AddWithValue(
                    "$id",
                    session.Id);

                int affectedRows =
                    command.ExecuteNonQuery();

                if (affectedRows ==
                    0)
                {
                    MessageBox.Show(
                        this,
                        "That session could not be found. It may have already been removed.",
                        "GameWatch",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                LoadAllData();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"GameWatch could not delete that session.\n\n{ex.Message}",
                    "GameWatch",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static Border CreateLauncherBadge(
            string launcher)
        {
            string displayLauncher =
                string.IsNullOrWhiteSpace(
                    launcher)
                    ? "Unknown"
                    : launcher;

            Border badge =
                new()
                {
                    Background =
                        MakeBrush(
                            29,
                            38,
                            33),

                    BorderBrush =
                        MakeBrush(
                            47,
                            59,
                            50),

                    BorderThickness =
                        new Thickness(1),

                    CornerRadius =
                        new CornerRadius(5),

                    Padding =
                        new Thickness(
                            7,
                            2,
                            7,
                            2),

                    VerticalAlignment =
                        VerticalAlignment.Center
                };

            badge.Child =
                new TextBlock
                {
                    Text =
                        displayLauncher,

                    Foreground =
                        MakeBrush(
                            124,
                            255,
                            107),

                    FontSize =
                        10,

                    FontWeight =
                        FontWeights.SemiBold
                };

            return badge;
        }

        private static TextBlock CreateMutedText(
            string text)
        {
            return new TextBlock
            {
                Text =
                    text,

                Foreground =
                    MakeBrush(
                        139,
                        148,
                        165),

                FontSize =
                    13
            };
        }

        private static SolidColorBrush MakeBrush(
            byte red,
            byte green,
            byte blue)
        {
            return new SolidColorBrush(
                Color.FromRgb(
                    red,
                    green,
                    blue));
        }

        private static DateTime GetStartOfWeek(
            DateTime date)
        {
            int difference =
                (7 +
                 (date.DayOfWeek -
                  DayOfWeek.Monday))
                % 7;

            return date
                .Date
                .AddDays(
                    -difference);
        }

        private static string FormatDuration(
            int totalSeconds)
        {
            TimeSpan duration =
                TimeSpan.FromSeconds(
                    totalSeconds);

            int hours =
                (int)duration.TotalHours;

            return
                $"{hours}h {duration.Minutes:00}m";
        }

        // ============================================================
        // MODELS
        // ============================================================

        private record GameSession(
            int Id,
            string GameName,
            string Launcher,
            DateTime StartTime,
            DateTime EndTime,
            int DurationSeconds);

        private record GameLibraryItem(
            string GameName,
            string Launcher,
            int SessionCount,
            int TotalSeconds,
            DateTime LastPlayed);

        private record StatBreakdownItem(
            string Name,
            int Seconds,
            int SessionCount);

        private record DailyStatItem(
            DateTime Date,
            int Seconds,
            int SessionCount);
        private record UpdateInfo(
            string Version,
            string DownloadUrl);

        private sealed class DashboardSettings
        {
            public bool MinimizeToTray { get; set; } = true;

            public bool NotificationsEnabled { get; set; } = true;
        }

    }
}
