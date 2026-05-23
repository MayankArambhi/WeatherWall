using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Net.Http;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Windows.Threading;
using System.Windows.Controls;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace WeatherWall
{
    // MEMORY: Model stores path, thumbnail is loaded on-demand
    public class WallpaperItem
    {
        public string FullPath { get; set; } = string.Empty;
        public string FileName => Path.GetFileName(FullPath);
        
        private BitmapSource? _thumbnail;
        public BitmapSource? Thumbnail 
        { 
            get 
            {
                if (_thumbnail == null) 
                {
                    // MEMORY: Load asynchronously to avoid UI stutter
                    _thumbnail = ThumbnailProvider.GetThumbnail(FullPath);
                }
                return _thumbnail;
            }
        }


        // Method to force release memory
        public void ClearThumbnail() => _thumbnail = null;
    }

    public class RuleItem
    {
        public WallpaperRule OriginalRule { get; set; } = new();
        public string FileName => OriginalRule.FileName;
        public string Weather => OriginalRule.Weather;
        public string TimePeriod => OriginalRule.TimePeriod;
        public string? FullPath { get; set; }

        public bool IsMissing => string.IsNullOrEmpty(FullPath) || !File.Exists(FullPath);
        public Visibility MissingVisibility => IsMissing ? Visibility.Visible : Visibility.Collapsed;
        public double Opacity => IsMissing ? 0.4 : 1.0;

        private BitmapSource? _thumbnail;
        public BitmapSource? Thumbnail 
        { 
            get 
            {
                if (_thumbnail == null && !string.IsNullOrEmpty(FullPath) && File.Exists(FullPath)) 
                    _thumbnail = ThumbnailProvider.GetThumbnail(FullPath);
                return _thumbnail;
            }
        }
        public void ClearThumbnail() => _thumbnail = null;
    }

    // MEMORY: Centralized thumbnail provider with size constraints
    public static class ThumbnailProvider
    {
        public static BitmapSource? GetThumbnail(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path);
                // MEMORY: Strict size limits and cache options
                bitmap.DecodePixelWidth = 160; 
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation;
                bitmap.EndInit();
                if (bitmap.CanFreeze) bitmap.Freeze();
                return bitmap;

            }
            catch { return null; }
        }
    }

    public partial class MainWindow : Window
    {
        private const string ConfigFileName = "config.json";
        private const string LogFileName = "weatherwall.log";
        private AppConfig _config = new();
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly DispatcherTimer _syncTimer = new();
        private string _currentAppliedWallpaper = string.Empty;
        private string _currentWeatherCategory = "unknown";
        private bool _isPaused = false;
        private Forms.NotifyIcon? _notifyIcon;
        private DateTime? _sunrise;
        private DateTime? _sunset;
        private string _currentTimeZone = "UTC";
        private FileSystemWatcher? _watcher;
        private readonly DispatcherTimer _watcherTimer = new();

        private readonly List<IWeatherProvider> _weatherProviders = new()
        {
            new OpenMeteoProvider(),
            new MetNorwayProvider(),
            new OpenWeatherMapProvider(),
            new WeatherApiProvider(),
            new TomorrowIoProvider(),
            new AccuWeatherProvider()
        };

        private List<ProviderWeatherResult> _latestResults = new();
        private DateTime? _lastSuccessfulSyncTime;
        private string _consensusDiagnosticsLog = "";
        private string _consensusConfidenceText = "0%";
        private string _consensusWeatherCategory = "unknown";
        private bool _isUpdatingUI = false;


        [ComImport]
        [Guid("C2CF3110-468E-4474-8350-59A9D0AB82BD")]
        public class DesktopWallpaper { }

        [ComImport]
        [Guid("B92B5679-D053-4A37-885E-5563D0C6B93F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID);
            [return: MarshalAs(UnmanagedType.LPWStr)]
            string GetMonitorDevicePathAt(uint monitorIndex);
            uint GetMonitorDevicePathCount();
            void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out Rect rect);
            void SetBackgroundColor(uint color);
            uint GetBackgroundColor();
            void SetPosition(int position);
            int GetPosition();
            void SetSlideshow(IntPtr IShellItemArray);
            IntPtr GetSlideshow();
            void SetSlideshowOptions(int options, uint slideshowTick);
            void GetSlideshowOptions(out int options, out uint slideshowTick);
            void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID, int direction);
            int GetStatus();
            void Enable(bool enable);
        }

        private IDesktopWallpaper? _desktopWallpaper;

        public MainWindow()
        {
            InitializeComponent();
            Log("Application Starting...");
            
            LoadConfig();
            SetupTrayIcon();
            
            try {
                _desktopWallpaper = (IDesktopWallpaper)new DesktopWallpaper();
            } catch (Exception ex) { Log($"COM Init Error: {ex.Message}"); }
            
            _syncTimer.Interval = TimeSpan.FromMinutes(1);
            _syncTimer.Tick += async (s, e) => await AutoSyncAsync();
            _syncTimer.Start();

            _watcherTimer.Interval = TimeSpan.FromSeconds(2);
            _watcherTimer.Tick += (s, e) => {
                _watcherTimer.Stop();
                if (!string.IsNullOrEmpty(_config.WallpaperFolderPath))
                    _ = Task.Run(() => ScanFolder(_config.WallpaperFolderPath));
            };

            _ = InitialSyncAsync();
            
            StartWithWindowsCheck.IsChecked = _config.StartWithWindows;
            AutoLocationCheck.IsChecked = _config.AutoLocation;
            LocationInput.Text = _config.LocationName;
            
            this.Closing += MainWindow_Closing;
            this.StateChanged += MainWindow_StateChanged;
            this.SourceInitialized += (s, e) => UpdateTitleBarTheme();

            ApplyTheme();

            if (_config.AutoLocation) _ = DetectLocationAsync();
            else PopulateCoordinateInputs();

            _ = CheckFirstLaunchAsync();
        }

        private void PopulateCoordinateInputs()
        {
            Dispatcher.Invoke(() => {
                LatInput.Text = _config.Latitude.ToString("F4");
                LonInput.Text = _config.Longitude.ToString("F4");
            });
        }

        private async Task CheckFirstLaunchAsync()
        {
            // Wait for splash screen to finish (3s) + extra buffer
            await Task.Delay(4000);

            if (string.IsNullOrEmpty(_config.WallpaperFolderPath) || !Directory.Exists(_config.WallpaperFolderPath))
            {
                Dispatcher.Invoke(() => {
                    var result = System.Windows.MessageBox.Show(
                        "Welcome to WeatherWall! \n\nPlease select a folder containing your wallpapers to get started.",
                        "First Launch",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Information);

                    if (result == MessageBoxResult.OK)
                    {
                        SelectFolder_Click(this, new RoutedEventArgs());
                    }
                });
            }
        }

        private async Task DetectLocationAsync()
        {
            try
            {
                Log("Detecting location...");
                
                // 1. Try Windows Location Services first (High Precision)
                bool windowsSuccess = await GetWindowsLocationAsync();
                if (windowsSuccess) 
                {
                    Log("Location detected via Windows Services.");
                    await SyncLocationUIAndWeatherAsync();
                    return;
                }

                // 2. Fallback to IP-based Geolocation (Low Precision)
                Log("Falling back to IP-based geolocation.");
                var response = await _httpClient.GetStringAsync("http://ip-api.com/json/");
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                if (root.GetProperty("status").GetString() == "success")
                {
                    _config.Latitude = root.GetProperty("lat").GetDouble();
                    _config.Longitude = root.GetProperty("lon").GetDouble();
                    _config.LocationName = root.GetProperty("city").GetString() ?? "Unknown";
                    await SyncLocationUIAndWeatherAsync();
                }
            }
            catch (Exception ex) { 
                Log($"Location Detection Error: {ex.Message}");
                Dispatcher.Invoke(() => {
                    StatusMatchedText.Text = "Location Error: Fallback to manual";
                });
            }
        }

        private async Task<bool> GetWindowsLocationAsync()
        {
            try
            {
                var accessStatus = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
                if (accessStatus == Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed)
                {
                    var geolocator = new Windows.Devices.Geolocation.Geolocator { 
                        DesiredAccuracyInMeters = 100,
                        ReportInterval = 0
                    };
                    
                    var pos = await geolocator.GetGeopositionAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    _config.Latitude = pos.Coordinate.Point.Position.Latitude;
                    _config.Longitude = pos.Coordinate.Point.Position.Longitude;
                    
                    // Attempt to get a better name via reverse geocoding
                    _config.LocationName = await GetLocationNameAsync(_config.Latitude, _config.Longitude);
                    return true;
                }
            }
            catch (Exception ex) { Log($"Windows Location API Error: {ex.Message}"); }
            return false;
        }

        private async Task<string> GetLocationNameAsync(double lat, double lon)
        {
            try
            {
                string url = $"https://geocoding-api.open-meteo.com/v1/reverse?latitude={lat}&longitude={lon}";
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                {
                    return results.EnumerateArray().First().GetProperty("name").GetString() ?? "My Location";
                }
            }
            catch { }
            return "My Location";
        }

        private async Task SyncLocationUIAndWeatherAsync()
        {
            SaveConfig();
            PopulateCoordinateInputs();
            Dispatcher.Invoke(() => LocationInput.Text = _config.LocationName);
            await UpdateWeatherAsync(true);
        }

        private void ApplyTheme()
        {
            // PROFESSIONAL: Neutral Dark Theme for minimal premium utility aesthetic
            var res = this.Resources;
            res["WindowBackgroundBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 11, 11));
            res["CardBackgroundBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 18, 18));
            res["PrimaryTextBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 248, 248));
            res["SecondaryTextBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128));
            res["BorderBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 34, 34));
            res["WeatherChipBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 32, 26));
            res["WeatherChipForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128));
            res["TimeChipBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 29, 29));
            res["TimeChipForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(170, 170, 170));
            
            UpdateTitleBarTheme();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        private void UpdateTitleBarTheme()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkMode = 1; // Always Dark
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                // On Windows 11, set the caption and text color explicitly
                int captionColor = 0x0B0B0B; // BGR format for #0B0B0B
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

                int textColor = 0xFFFFFF;
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
            }
            catch { /* Not supported on older Windows versions */ }
        }

        private void SetupTrayIcon()
        {
            try
            {
                _notifyIcon = new Forms.NotifyIcon();
                
                // Load from Small_NoBG.ico resource for maximum compatibility
                var iconUri = new Uri("pack://application:,,,/icon_.ico");
                var streamInfo = System.Windows.Application.GetResourceStream(iconUri);
                if (streamInfo != null)
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(streamInfo.Stream);
                }
                else
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                }

                _notifyIcon.Visible = true;
                _notifyIcon.Text = "WeatherWall";
                _notifyIcon.DoubleClick += (s, e) => ShowWindow();

                var contextMenu = new Forms.ContextMenuStrip();
                contextMenu.Items.Add("Open WeatherWall", null, (s, e) => ShowWindow());
                contextMenu.Items.Add("Sync Now", null, async (s, e) => await AutoSyncAsync(true));
                contextMenu.Items.Add(new Forms.ToolStripSeparator());
                contextMenu.Items.Add("Exit", null, (s, e) => {
                    _notifyIcon?.Dispose();
                    System.Windows.Application.Current.Shutdown();
                });
                _notifyIcon.ContextMenuStrip = contextMenu;
            }
            catch (Exception ex)
            {
                Log($"Tray Icon Setup Error: {ex.Message}");
                if (_notifyIcon != null)
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                    _notifyIcon.Visible = true;
                }
            }
        }

        private void TogglePause()
        {
            _isPaused = !_isPaused;
            StatusModeText.Text = _isPaused ? "PAUSED" : "RUNNING";
            AutoSyncLabel.Foreground = _isPaused ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128));
            _config.IsPaused = _isPaused;
            SaveConfig();
        }

        private void ShowWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
            // Reload thumbnails when window is shown
            RefreshUI();
        }

        private void RefreshUI()
        {
            Dispatcher.Invoke(() => {
                // Rebuild UI lazily when window is shown
                if (!string.IsNullOrEmpty(_config.WallpaperFolderPath) && Directory.Exists(_config.WallpaperFolderPath))
                {
                    // ScanFolder will automatically update FileListBox, RuleWallpaperGallery, and call RefreshRulesList()
                    _ = Task.Run(() => ScanFolder(_config.WallpaperFolderPath));
                }
                else
                {
                    RefreshRulesList();
                    NoWallpapersState.Visibility = Visibility.Visible;
                }
            });
        }


        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
            // MEMORY: Aggressive cleanup when UI is not visible
            FlushMemory();
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide();
                FlushMemory();
            }
            else if (this.WindowState == WindowState.Normal)
            {
                RefreshUI();
            }
        }


        private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is System.Windows.Controls.TabControl)
            {
                // Removed FlushMemory() call to eliminate 2-3s switching lag.
                // GC collection is expensive and should only happen when minimized.
            }
        }

        private void FlushMemory()
        {
            try
            {
                // Unload heavy visual trees and image references completely
                Dispatcher.Invoke(() => {
                    FileListBox.ItemsSource = null;
                    RuleWallpaperGallery.ItemsSource = null;
                    ActiveRulesListBox.ItemsSource = null;
                });

                // Run garbage collection
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                
                // Clear working set to release memory back to OS immediately
                SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
            }
            catch { }
        }

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, int dwMinimumWorkingSetSize, int dwMaximumWorkingSetSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();


        private void Log(string message)
        {
            try
            {
                string fullLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LogFileName);
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                File.AppendAllText(fullLogPath, entry);
            }
            catch { }
        }

        private async Task InitialSyncAsync()
        {
            await UpdateWeatherAsync();
            await ApplyRuleBasedWallpaperAsync();
        }

        private async Task AutoSyncAsync(bool forceRefresh = false)
        {
            if (_isPaused) return;
            await UpdateWeatherAsync(forceRefresh);
            await ApplyRuleBasedWallpaperAsync();
            
            string timeStr = _lastSuccessfulSyncTime.HasValue ? _lastSuccessfulSyncTime.Value.ToString("HH:mm:ss") : "Never";
            Dispatcher.Invoke(() => StatusLastUpdateText.Text = $"Last Sync: {timeStr}");
        }

        private async Task UpdateWeatherAsync(bool forceRefresh = false)
        {
            // Caching check
            if (!forceRefresh && _lastSuccessfulSyncTime.HasValue &&
                DateTime.Now - _lastSuccessfulSyncTime.Value < TimeSpan.FromMinutes(15) &&
                _latestResults.Count > 0)
            {
                Log("Using cached weather data (fetched <15m ago).");
                ApplyConsensusAndOverride();
                return;
            }

            Log($"Refreshing weather data (force={forceRefresh}, Lat={_config.Latitude:F4}, Lon={_config.Longitude:F4})...");

            var fetchTasks = new List<Task<ProviderWeatherResult>>();
            foreach (var provider in _weatherProviders)
            {
                string key = provider.Name switch
                {
                    "OpenWeatherMap" => _config.OpenWeatherMapKey,
                    "WeatherAPI" => _config.WeatherApiKey,
                    "Tomorrow.io" => _config.TomorrowIoKey,
                    "AccuWeather" => _config.AccuWeatherKey,
                    _ => ""
                };

                if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                fetchTasks.Add(Task.Run(async () => {
                    try
                    {
                        return await provider.GetWeatherAsync(_httpClient, _config.Latitude, _config.Longitude, key);
                    }
                    catch (Exception ex)
                    {
                        return new ProviderWeatherResult
                        {
                            ProviderName = provider.Name,
                            Success = false,
                            ErrorMessage = ex.Message
                        };
                    }
                }));
            }

            if (fetchTasks.Count == 0)
            {
                fetchTasks.Add(new OpenMeteoProvider().GetWeatherAsync(_httpClient, _config.Latitude, _config.Longitude));
            }

            var results = await Task.WhenAll(fetchTasks);
            _latestResults = results.ToList();

            var successfulResults = _latestResults.Where(r => r.Success).ToList();
            if (successfulResults.Count > 0)
            {
                _lastSuccessfulSyncTime = DateTime.Now;

                // 1. Filter out stale results (older than 2 hours compared to the latest available observation)
                var latestObsTime = successfulResults.Max(r => r.ObservationTime ?? DateTime.Now);
                var freshResults = successfulResults.Where(r => r.ObservationTime == null || 
                    (latestObsTime - r.ObservationTime.Value).Duration() <= TimeSpan.FromHours(2)).ToList();
                if (freshResults.Count == 0) freshResults = successfulResults;

                // 2. Aggregate metrics for consensus comparison
                double avgCloudCover = 0;
                int ccCount = 0;
                double avgPrecip = 0;
                int precipCount = 0;

                int thunderVotes = 0;
                int snowVotes = 0;
                int fogVotes = 0;
                int rainyVotes = 0;

                foreach (var r in freshResults)
                {
                    if (r.CloudCover.HasValue) { avgCloudCover += r.CloudCover.Value; ccCount++; }
                    if (r.Precipitation.HasValue) { avgPrecip += r.Precipitation.Value; precipCount++; }
                    
                    if (r.InterpretedCondition == "thunderstorm") thunderVotes++;
                    else if (r.InterpretedCondition == "snowy") snowVotes++;
                    else if (r.InterpretedCondition == "foggy") fogVotes++;
                    else if (r.InterpretedCondition == "rainy") rainyVotes++;
                }

                if (ccCount > 0) avgCloudCover /= ccCount;
                if (precipCount > 0) avgPrecip /= precipCount;

                // 3. Robust, Conservative consensus logic to avoid aggressive or extreme weather classifications
                bool isThunderstorm = false;
                bool isSnowy = false;
                bool isFoggy = false;
                bool isRainy = false;

                // A. Thunderstorm: Require strong confirmation
                // Must have at least 2 independent providers voting thunderstorm OR if only 1 provider is available, it must be highly confirmed with precip > 0
                if (thunderVotes > 0)
                {
                    if (freshResults.Count >= 2)
                    {
                        if (thunderVotes >= 2) isThunderstorm = true;
                    }
                    else
                    {
                        var r = freshResults[0];
                        if (r.Precipitation.HasValue && r.Precipitation.Value > 0)
                        {
                            isThunderstorm = true;
                        }
                    }
                }

                // B. Snowy: Require snow votes (at least 2 if multiple, or 1 if single)
                if (snowVotes > 0)
                {
                    if (freshResults.Count >= 2)
                    {
                        if (snowVotes >= 2 || (double)snowVotes / freshResults.Count >= 0.5) isSnowy = true;
                    }
                    else
                    {
                        isSnowy = true;
                    }
                }

                // C. Foggy: Require fog votes (at least 2 if multiple, or 1 if single)
                if (fogVotes > 0)
                {
                    if (freshResults.Count >= 2)
                    {
                        if (fogVotes >= 2 || (double)fogVotes / freshResults.Count >= 0.5) isFoggy = true;
                    }
                    else
                    {
                        isFoggy = true;
                    }
                }

                // D. Rainy: Do not classify rainy unless actual current precipitation exists (> 0 mm)
                // and is supported by at least one provider voting rainy/thunderstorm.
                if (avgPrecip > 0 && (rainyVotes > 0 || thunderVotes > 0))
                {
                    if (freshResults.Count >= 2)
                    {
                        int positivePrecipCount = freshResults.Count(r => r.Precipitation.HasValue && r.Precipitation.Value > 0);
                        // At least half of the fresh providers must see some precipitation, or average precip must be measurable (>0.2mm)
                        if ((double)positivePrecipCount / freshResults.Count >= 0.5 || avgPrecip > 0.2)
                        {
                            isRainy = true;
                        }
                    }
                    else
                    {
                        isRainy = true;
                    }
                }

                string finalCondition = "clear";
                if (isThunderstorm)
                {
                    finalCondition = "thunderstorm";
                }
                else if (isSnowy)
                {
                    finalCondition = "snowy";
                }
                else if (isRainy)
                {
                    finalCondition = "rainy";
                }
                else if (isFoggy)
                {
                    finalCondition = "foggy";
                }
                else
                {
                    // Conservative cloud-cover mapping:
                    // 0-25% -> clear
                    // 25-60% -> partly_cloudy
                    // 60-85% -> cloudy
                    // 85%+ -> overcast
                    if (avgCloudCover <= 25) finalCondition = "clear";
                    else if (avgCloudCover <= 60) finalCondition = "partly_cloudy";
                    else if (avgCloudCover <= 85) finalCondition = "cloudy";
                    else finalCondition = "overcast";
                }

                // Prefer safer conditions when confidence is low
                int agreementCount = freshResults.Count(r => r.InterpretedCondition == finalCondition);
                double agreementRatio = (double)agreementCount / freshResults.Count;

                if (agreementRatio < 0.5 && finalCondition == "overcast")
                {
                    // Downgrade overcast to cloudy if confidence is low
                    finalCondition = "cloudy";
                }
                else if (agreementRatio < 0.33 && finalCondition == "cloudy")
                {
                    // Downgrade cloudy to partly_cloudy if confidence is extremely low
                    finalCondition = "partly_cloudy";
                }

                _consensusWeatherCategory = finalCondition;
                _consensusConfidenceText = $"{(int)(agreementRatio * 100)}%";

                var openMeteoRes = successfulResults.FirstOrDefault(r => r.ProviderName == "Open-Meteo");
                if (openMeteoRes != null)
                {
                    if (openMeteoRes.Sunrise.HasValue) _sunrise = openMeteoRes.Sunrise;
                    if (openMeteoRes.Sunset.HasValue) _sunset = openMeteoRes.Sunset;
                    _currentTimeZone = openMeteoRes.Timezone;
                }
            }
            else
            {
                _consensusWeatherCategory = "unknown";
                _consensusConfidenceText = "0%";
                Log("All weather providers failed to fetch data.");
            }

            ApplyConsensusAndOverride();

            // Build beautifully detailed, formatted diagnostics log
            _consensusDiagnosticsLog = $"=== WEATHER DIAGNOSTIC SYSTEM ===\r\n" +
                                      $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                                      $"Consensus Weather: {WeatherMapper.GetFriendlyName(_consensusWeatherCategory)} ({_consensusWeatherCategory})\r\n" +
                                      $"Confidence Level: {_consensusConfidenceText}\r\n" +
                                      $"=================================\r\n\r\n" +
                                      $"Provider Performance & Metrics:\r\n\r\n";

            foreach (var r in _latestResults)
            {
                _consensusDiagnosticsLog += $"[ {r.ProviderName} ]\r\n" +
                                           $"  - Status: {(r.Success ? "ONLINE" : "OFFLINE")}\r\n";
                if (r.Success)
                {
                    _consensusDiagnosticsLog += $"  - Raw Code: {r.RawCode}\r\n" +
                                               $"  - Raw Description: {r.RawDescription}\r\n" +
                                               $"  - Temperature: {(r.Temperature.HasValue ? $"{r.Temperature.Value:F1}°C" : "N/A")}\r\n" +
                                               $"  - Cloud Cover: {(r.CloudCover.HasValue ? $"{r.CloudCover.Value:F0}%" : "N/A")}\r\n" +
                                               $"  - Precipitation: {(r.Precipitation.HasValue ? $"{r.Precipitation.Value:F2} mm" : "N/A")}\r\n" +
                                               $"  - Rain Probability: {(r.RainProbability.HasValue ? $"{r.RainProbability.Value:F0}%" : "N/A")}\r\n" +
                                               $"  - Observation/Update Time: {(r.ObservationTime.HasValue ? r.ObservationTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "N/A")}\r\n" +
                                               $"  - Interpreted WeatherWall Condition: {WeatherMapper.GetFriendlyName(r.InterpretedCondition)} ({r.InterpretedCondition})\r\n";
                }
                else
                {
                    _consensusDiagnosticsLog += $"  - Error: {r.ErrorMessage}\r\n";
                }
                _consensusDiagnosticsLog += "\r\n";
            }
            Log(_consensusDiagnosticsLog);
        }

        private void ApplyConsensusAndOverride()
        {
            string activeCondition = _consensusWeatherCategory;
            bool isOverrideActive = false;

            if (_config.ManualOverrideExpires.HasValue && 
                _config.ManualOverrideExpires.Value > DateTime.Now && 
                !string.IsNullOrEmpty(_config.ManualOverrideWeather))
            {
                activeCondition = _config.ManualOverrideWeather;
                isOverrideActive = true;
            }

            _currentWeatherCategory = activeCondition;

            string friendlyName = WeatherMapper.GetFriendlyName(activeCondition);
            string icon = WeatherMapper.GetIcon(activeCondition);

            Dispatcher.Invoke(() => {
                string mainStatus = isOverrideActive ? $"{friendlyName} (Manual Override)" : friendlyName;
                
                double? consensusTemp = _latestResults.Where(r => r.Success).Select(r => r.Temperature).FirstOrDefault();
                if (consensusTemp.HasValue)
                {
                    WeatherStatusText.Text = $"{mainStatus} ({consensusTemp.Value:0}°C)";
                }
                else
                {
                    WeatherStatusText.Text = mainStatus;
                }
                WeatherIconText.Text = icon;

                string timePeriod = GetCurrentTimePeriod();
                StatusConditionText.Text = $"{_config.LocationName.ToUpper()} · {activeCondition.Replace("_", " ").ToUpper()} · {timePeriod.ToUpper()}";

                string overrideStatusText = isOverrideActive 
                    ? $"[OVERRIDE ACTIVE] Expires: {_config.ManualOverrideExpires:HH:mm:ss}" 
                    : $"Consensus: {friendlyName} (Confidence: {_consensusConfidenceText})";
                
                StatusMatchedText.Text = $"{_config.LocationName} · {friendlyName} · {timePeriod} · {overrideStatusText}";
                
                var tooltipContent = $"Location: {_config.LocationName} ({_config.Latitude:F4}, {_config.Longitude:F4})\n" +
                                     $"Current Time Period: {timePeriod}\n" +
                                     $"Manual Override: {(isOverrideActive ? $"Yes ({friendlyName}, expires at {_config.ManualOverrideExpires:HH:mm:ss})" : "No")}\n" +
                                     $"Consensus Condition: {WeatherMapper.GetFriendlyName(_consensusWeatherCategory)}\n" +
                                     $"Consensus Confidence: {_consensusConfidenceText}\n" +
                                     $"Last API Fetch: {(_lastSuccessfulSyncTime.HasValue ? _lastSuccessfulSyncTime.Value.ToString("HH:mm:ss") : "Never")}\n\n" +
                                     $"Provider Statuses:\n";

                foreach (var r in _latestResults)
                {
                    if (r.Success)
                    {
                        tooltipContent += $"• {r.ProviderName}: {WeatherMapper.GetFriendlyName(r.InterpretedCondition)} | Temp: {r.Temperature:0}°C | Cloud: {r.CloudCover:0}% | Precip: {r.Precipitation:0.##}mm\n";
                    }
                    else
                    {
                        tooltipContent += $"• {r.ProviderName}: Offline ({r.ErrorMessage})\n";
                    }
                }
                
                StatusMatchedText.ToolTip = new System.Windows.Controls.ToolTip { Content = tooltipContent };

                UpdateDiagnosticsTabUI();
            });
        }

        private void UpdateDiagnosticsTabUI()
        {
            try
            {
                if (ConsensusConditionText == null) return;

                _isUpdatingUI = true;

                if (ConsensusLocationText != null)
                {
                    ConsensusLocationText.Text = !string.IsNullOrEmpty(_config.LocationName) ? _config.LocationName : "Unknown Location";
                }

                ConsensusConditionText.Text = WeatherMapper.GetFriendlyName(_consensusWeatherCategory);
                ConsensusConfidenceText.Text = _consensusConfidenceText;
                LastSuccessfulSyncText.Text = _lastSuccessfulSyncTime.HasValue 
                    ? _lastSuccessfulSyncTime.Value.ToString("HH:mm:ss") 
                    : "Never";

                if (_lastSuccessfulSyncTime.HasValue && DateTime.Now - _lastSuccessfulSyncTime.Value < TimeSpan.FromMinutes(15))
                {
                    CacheStatusText.Text = $"Cached (Expires in {TimeSpan.FromMinutes(15) - (DateTime.Now - _lastSuccessfulSyncTime.Value):mm\\:ss})";
                    CacheStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 150, 150));
                }
                else
                {
                    CacheStatusText.Text = "Stale / Refresh Needed";
                    CacheStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
                }

                bool isOverrideActive = _config.ManualOverrideExpires.HasValue && 
                                       _config.ManualOverrideExpires.Value > DateTime.Now && 
                                       !string.IsNullOrEmpty(_config.ManualOverrideWeather);

                ManualOverrideCheckBox.IsChecked = isOverrideActive;
                if (isOverrideActive && _config.ManualOverrideExpires.HasValue)
                {
                    DateTime expires = _config.ManualOverrideExpires.Value;
                    OverrideExpiresText.Text = $"Expires at {expires:HH:mm:ss} ({(expires - DateTime.Now):hh\\:mm\\:ss} left)";
                    OverrideExpiresText.Visibility = Visibility.Visible;
                    foreach (ComboBoxItem item in OverrideWeatherCombo.Items)
                    {
                        if (item.Content?.ToString()?.ToLower()?.Replace(" ", "_") == _config.ManualOverrideWeather)
                        {
                            OverrideWeatherCombo.SelectedItem = item;
                            break;
                        }
                    }
                }
                else
                {
                    OverrideExpiresText.Text = "";
                    OverrideExpiresText.Visibility = Visibility.Collapsed;
                }

                DiagnosticsLogsText.Text = _consensusDiagnosticsLog;

                if (string.IsNullOrEmpty(OpenWeatherMapKeyInput.Password) && !string.IsNullOrEmpty(_config.OpenWeatherMapKey))
                    OpenWeatherMapKeyInput.Password = _config.OpenWeatherMapKey;
                if (string.IsNullOrEmpty(WeatherApiKeyInput.Password) && !string.IsNullOrEmpty(_config.WeatherApiKey))
                    WeatherApiKeyInput.Password = _config.WeatherApiKey;
                if (string.IsNullOrEmpty(TomorrowIoKeyInput.Password) && !string.IsNullOrEmpty(_config.TomorrowIoKey))
                    TomorrowIoKeyInput.Password = _config.TomorrowIoKey;
                if (string.IsNullOrEmpty(AccuWeatherKeyInput.Password) && !string.IsNullOrEmpty(_config.AccuWeatherKey))
                    AccuWeatherKeyInput.Password = _config.AccuWeatherKey;
            }
            catch { }
            finally
            {
                _isUpdatingUI = false;
            }
        }


        private string GetCurrentTimePeriod()
        {
            DateTime now = DateTime.Now;
            
            if (_sunrise.HasValue && _sunset.HasValue)
            {
                // PROFESSIONAL: Use TimeOfDay to avoid date-drift issues (e.g. 12 AM transitions)
                TimeSpan currentTime = now.TimeOfDay;
                TimeSpan sunriseTime = _sunrise.Value.TimeOfDay;
                TimeSpan sunsetTime = _sunset.Value.TimeOfDay;

                TimeSpan morningStart = sunriseTime;
                TimeSpan afternoonStart = new TimeSpan(11, 0, 0);
                TimeSpan eveningStart = sunsetTime.Subtract(TimeSpan.FromMinutes(90));
                TimeSpan nightStart = sunsetTime.Add(TimeSpan.FromMinutes(60));

                if (currentTime >= morningStart && currentTime < afternoonStart) return "morning";
                if (currentTime >= afternoonStart && currentTime < eveningStart) return "afternoon";
                if (currentTime >= eveningStart && currentTime < nightStart) return "evening";
                
                // Night is before morning or after nightStart
                return "night";
            }

            // Fallback (Fixed ranges)
            int hour = now.Hour;
            if (hour >= 6 && hour < 12) return "morning";
            if (hour >= 12 && hour < 17) return "afternoon";
            if (hour >= 17 && hour < 21) return "evening";
            return "night";
        }

        private (string status, string icon, string category) MapWeatherCode(int code)
        {
            // WMO Weather interpretation codes (WW)
            // https://open-meteo.com/en/docs
            return code switch
            {
                0 => ("Clear", "☀️", "clear"),
                1 or 2 => ("Partly Cloudy", "⛅", "partly_cloudy"),
                3 => ("Overcast", "☁️", "overcast"),
                45 or 48 => ("Foggy", "🌫️", "foggy"),
                51 or 53 or 55 or 56 or 57 => ("Drizzle", "🌦️", "drizzle"),
                61 or 63 or 65 or 66 or 67 => ("Rainy", "🌧️", "rainy"),
                80 or 81 or 82 => ("Rain Showers", "🌧️", "rainy"),
                71 or 73 or 75 or 77 or 85 or 86 => ("Snowy", "❄️", "snowy"),
                95 or 96 or 99 => ("Thunderstorm", "⛈️", "thunderstorm"),
                
                // FALLBACK: If code is unknown, default to a neutral "Cloudy" state 
                // instead of keeping a potentially extreme previous state like Thunderstorm.
                _ => ("Cloudy", "☁️", "overcast")
            };
        }

        private void LoadConfig()
        {
            try
            {
                string fullConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
                if (File.Exists(fullConfigPath))
                {
                    string json = File.ReadAllText(fullConfigPath);
                    var loadedConfig = JsonSerializer.Deserialize<AppConfig>(json);
                    if (loadedConfig != null) _config = loadedConfig;
                    
                    CleanupDuplicateRules();
                }
                else
                {
                    // First launch: Save default config
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                Log($"Config Load Error: {ex.Message}");
                _config = new AppConfig();
            }
            
            _isPaused = _config.IsPaused;
            Dispatcher.Invoke(() => {
                StatusModeText.Text = _isPaused ? "PAUSED" : "RUNNING";
                if (!string.IsNullOrEmpty(_config.WallpaperFolderPath) && Directory.Exists(_config.WallpaperFolderPath))
                {
                    FolderPathText.Text = _config.WallpaperFolderPath;
                    _ = Task.Run(() => ScanFolder(_config.WallpaperFolderPath));
                }
                RefreshRulesList();
            });
        }

        private void CleanupDuplicateRules()
        {
            if (_config.Rules == null || _config.Rules.Count == 0) return;

            var uniqueRules = new List<WallpaperRule>();
            bool duplicatesFound = false;

            // Keep only the most recently added rule (last in the list) for each condition
            foreach (var rule in _config.Rules)
            {
                var existing = uniqueRules.FirstOrDefault(r => r.Weather == rule.Weather && r.TimePeriod == rule.TimePeriod);
                if (existing != null)
                {
                    uniqueRules.Remove(existing);
                    duplicatesFound = true;
                }
                uniqueRules.Add(rule);
            }

            if (duplicatesFound)
            {
                _config.Rules = uniqueRules;
                SaveConfig();
                Log("Cleaned up duplicate rules from configuration.");
            }
        }

        private void SaveConfig()
        {
            string fullConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
            string tempPath = fullConfigPath + ".tmp";
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_config, options);
                File.WriteAllText(tempPath, json);
                if (File.Exists(fullConfigPath)) File.Delete(fullConfigPath);
                File.Move(tempPath, fullConfigPath);
            }
            catch (Exception ex)
            {
                Log($"Config Save Error: {ex.Message}");
                if (File.Exists(tempPath)) try { File.Delete(tempPath); } catch { }
            }
        }

        private async void UpdateLocation_Click(object sender, RoutedEventArgs e)
        {
            string query = LocationInput.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;

            try
            {
                Log($"Searching for location: {query}");
                // Use Open-Meteo Geocoding API
                string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=1&language=en&format=json";
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
                {
                    var first = results.EnumerateArray().First();
                    _config.Latitude = first.GetProperty("latitude").GetDouble();
                    _config.Longitude = first.GetProperty("longitude").GetDouble();
                    _config.LocationName = first.GetProperty("name").GetString() ?? query;
                    _config.AutoLocation = false;
                    AutoLocationCheck.IsChecked = false;
                    
                    await SyncLocationUIAndWeatherAsync();
                    System.Windows.MessageBox.Show($"Location updated to {_config.LocationName}.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    System.Windows.MessageBox.Show("Location not found. Please try another name.", "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Log($"Geocoding Error: {ex.Message}");
                System.Windows.MessageBox.Show("Error searching for location. Please check your connection.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SetCoordinates_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(LatInput.Text, out double lat) && double.TryParse(LonInput.Text, out double lon))
            {
                _config.Latitude = lat;
                _config.Longitude = lon;
                _config.AutoLocation = false;
                AutoLocationCheck.IsChecked = false;
                
                // Get location name for these coordinates
                _config.LocationName = await GetLocationNameAsync(lat, lon);
                
                await SyncLocationUIAndWeatherAsync();
                System.Windows.MessageBox.Show($"Coordinates applied: {lat}, {lon}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show("Invalid coordinates. Please enter numeric values.", "Input Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AutoLocation_Click(object sender, RoutedEventArgs e)
        {
            _config.AutoLocation = AutoLocationCheck.IsChecked ?? false;
            SaveConfig();
            if (_config.AutoLocation) _ = DetectLocationAsync();
        }

        private void SelectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Wallpaper Folder",
                InitialDirectory = _config.WallpaperFolderPath ?? string.Empty
            };

            if (dialog.ShowDialog() == true)
            {
                string folderPath = dialog.FolderName;
                FolderPathText.Text = folderPath;
                _config.WallpaperFolderPath = folderPath;
                SaveConfig();
                _ = Task.Run(() => ScanFolder(folderPath));
            }
        }

        private void ScanFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) 
            {
                Dispatcher.Invoke(() => {
                    FileListBox.ItemsSource = null;
                    RuleWallpaperGallery.ItemsSource = null;
                    NoWallpapersState.Visibility = Visibility.Visible;
                });
                return;
            }

            // Setup watcher if path changed or not yet initialized
            if (_watcher == null || _watcher.Path != path)
            {
                try
                {
                    _watcher?.Dispose();
                    _watcher = new FileSystemWatcher(path);
                    _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName;
                    _watcher.Filter = "*.*";
                    
                    // Use debounce timer to avoid flickering during batch operations
                    FileSystemEventHandler handler = (s, e) => Dispatcher.Invoke(() => { _watcherTimer.Stop(); _watcherTimer.Start(); });
                    RenamedEventHandler renamedHandler = (s, e) => Dispatcher.Invoke(() => { _watcherTimer.Stop(); _watcherTimer.Start(); });
                    
                    _watcher.Created += handler;
                    _watcher.Deleted += handler;
                    _watcher.Changed += handler;
                    _watcher.Renamed += renamedHandler;
                    _watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex) { Log($"Watcher Error: {ex.Message}"); }
            }

            string[] extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            try
            {
                var files = Directory.EnumerateFiles(path)
                    .Where(file => extensions.Contains(Path.GetExtension(file).ToLower()))
                    .OrderBy(f => Path.GetFileName(f))
                    .ToList();
                
                var items = files.Select(file => new WallpaperItem { FullPath = file }).ToList();
                
                Dispatcher.Invoke(() => {
                    // Update main list
                    FileListBox.ItemsSource = items;
                    
                    // Update rule selection gallery
                    RuleWallpaperGallery.ItemsSource = items.ToList();
                    
                    NoWallpapersState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    
                    RefreshRulesList();
                });
            }
            catch (Exception ex) { Log($"Scan Error: {ex.Message}"); }
        }

        private void FileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void SaveApiKeys_Click(object sender, RoutedEventArgs e)
        {
            _config.OpenWeatherMapKey = OpenWeatherMapKeyInput.Password.Trim();
            _config.WeatherApiKey = WeatherApiKeyInput.Password.Trim();
            _config.TomorrowIoKey = TomorrowIoKeyInput.Password.Trim();
            _config.AccuWeatherKey = AccuWeatherKeyInput.Password.Trim();
            SaveConfig();
            
            System.Windows.MessageBox.Show("API Keys saved successfully. Refreshing weather...", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = UpdateWeatherAsync(true);
        }

        private void RefreshWeather_Click(object sender, RoutedEventArgs e)
        {
            Log("User requested manual weather refresh.");
            _ = AutoSyncAsync(true);
        }

        private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(DiagnosticsLogsText.Text);
                System.Windows.MessageBox.Show("Diagnostics logs copied to clipboard.", "Diagnostics Copy", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to copy diagnostics: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ManualOverride_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;

            if (OverrideWeatherCombo.SelectedItem is ComboBoxItem item)
            {
                string targetWeather = item.Content.ToString() ?? "Clear";
                _config.ManualOverrideWeather = targetWeather.ToLower().Replace(" ", "_");
                _config.ManualOverrideExpires = DateTime.Now.AddHours(4); // Expiry in 4 hours
                SaveConfig();
                ApplyConsensusAndOverride();
                _ = ApplyRuleBasedWallpaperAsync();
            }
        }

        private void ManualOverride_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;

            _config.ManualOverrideWeather = "";
            _config.ManualOverrideExpires = null;
            SaveConfig();
            ApplyConsensusAndOverride();
            _ = ApplyRuleBasedWallpaperAsync();
        }

        private void OverrideWeatherCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;

            if (ManualOverrideCheckBox != null && ManualOverrideCheckBox.IsChecked == true && OverrideWeatherCombo.SelectedItem is ComboBoxItem item)
            {
                string targetWeather = item.Content.ToString() ?? "Clear";
                _config.ManualOverrideWeather = targetWeather.ToLower().Replace(" ", "_");
                _config.ManualOverrideExpires = DateTime.Now.AddHours(4);
                SaveConfig();
                ApplyConsensusAndOverride();
                _ = ApplyRuleBasedWallpaperAsync();
            }
        }

        private async void SetWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.DataContext is WallpaperItem item)
            {
                await ApplyWallpaperAsync(item.FullPath);
            }
        }

        private void CreateRule_Click(object sender, RoutedEventArgs e)
        {
            if (RuleWallpaperGallery.SelectedItem is WallpaperItem item)
            {
                string weather = (RuleWeatherCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "clear";
                string time = (RuleTimeCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "morning";

                _config.Rules.RemoveAll(r => r.Weather == weather && r.TimePeriod == time);
                _config.Rules.Add(new WallpaperRule { Weather = weather, TimePeriod = time, FileName = item.FileName });
                
                SaveConfig();
                RefreshRulesList();
                _ = ApplyRuleBasedWallpaperAsync();
                
                System.Windows.MessageBox.Show($"Rule created for {weather} + {time}.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show("Please select a wallpaper from the gallery.", "Selection Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RefreshRulesList()
        {
            var items = new List<RuleItem>();
            foreach (var rule in _config.Rules.OrderBy(r => r.Weather).ThenBy(r => r.TimePeriod))
            {
                string? fullPath = null;
                if (!string.IsNullOrEmpty(_config.WallpaperFolderPath))
                    fullPath = Path.Combine(_config.WallpaperFolderPath, rule.FileName);

                items.Add(new RuleItem {
                    OriginalRule = rule,
                    FullPath = fullPath
                });
            }
            ActiveRulesListBox.ItemsSource = items;
            NoRulesState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DeleteRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.DataContext is RuleItem item)
            {
                _config.Rules.Remove(item.OriginalRule);
                SaveConfig();
                RefreshRulesList();
            }
        }

        private async void PreviewRule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.DataContext is RuleItem item && !string.IsNullOrEmpty(item.FullPath))
            {
                await ApplyWallpaperAsync(item.FullPath);
            }
        }

        private void TaggingMode_Changed(object sender, RoutedEventArgs e)
        {
            if (ManualTaggingPanel == null || AITaggingPanel == null) return;

            if (ManualModeRadio.IsChecked == true)
            {
                ManualTaggingPanel.Visibility = Visibility.Visible;
                AITaggingPanel.Visibility = Visibility.Collapsed;
            }
            else if (AIModeRadio.IsChecked == true)
            {
                ManualTaggingPanel.Visibility = Visibility.Collapsed;
                AITaggingPanel.Visibility = Visibility.Visible;
            }
        }

        private List<SuggestedRule> _suggestedMatches = new();
        private readonly AITaggingService _aiTaggingService = new();

        private async void GenerateTags_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_config.WallpaperFolderPath) || !Directory.Exists(_config.WallpaperFolderPath))
            {
                System.Windows.MessageBox.Show("Please select a wallpaper folder in the Library tab first.", "Folder Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GenerateTagsBtn.IsEnabled = false;
            AITagResultsPanel.Children.Clear();
            AITagResultsPanel.Children.Add(new TextBlock { Text = "Extracting semantic descriptors and evaluating conditions matrix...", Foreground = new SolidColorBrush(Colors.Gray), Margin = new Thickness(0,0,0,8) });
            
            _suggestedMatches.Clear();
            CreateSuggestedRulesBtn.IsEnabled = false;

            await Task.Run(async () => 
            {
                await Task.Delay(500); // Simulate init delay
                
                var results = _aiTaggingService.AnalyzeLibrary(_config.WallpaperFolderPath);
                _suggestedMatches = results;

                foreach (var match in results)
                {
                    Dispatcher.Invoke(() => {
                        var container = new StackPanel { Margin = new Thickness(0,0,0,16) };
                        container.Children.Add(new TextBlock { Text = $"Condition: {match.Weather.ToUpper()} + {match.TimePeriod.ToUpper()}", FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220,220,220)) });
                        
                        var bestTb = new TextBlock { Margin = new Thickness(8,4,0,0) };
                        if (match.Confidence.NeedsReview)
                        {
                            bestTb.Text = $"Best match: {match.BestFileName} (Confidence: {match.Confidence.Score}% - Needs Review)";
                            bestTb.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 179, 8)); // Yellow for low conf
                        }
                        else
                        {
                            bestTb.Text = $"Best match: {match.BestFileName} (Confidence: {match.Confidence.Score}%)";
                            bestTb.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128)); // Green
                        }
                        container.Children.Add(bestTb);

                        if (match.Alternatives.Any())
                        {
                            container.Children.Add(new TextBlock { Text = $"Alternatives: {string.Join(", ", match.Alternatives.Take(2))}" + (match.Alternatives.Count > 2 ? "..." : ""), Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(150,150,150)), Margin = new Thickness(8,2,0,0), FontSize = 11 });
                        }
                        
                        AITagResultsPanel.Children.Add(container);
                    });
                    await Task.Delay(50); 
                }
            });

            AITagResultsPanel.Children.Add(new TextBlock { Text = "Analysis Complete.", Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(74, 222, 128)), FontWeight = FontWeights.Bold, Margin = new Thickness(0,8,0,0) });
            CreateSuggestedRulesBtn.IsEnabled = _suggestedMatches.Count > 0;
            GenerateTagsBtn.IsEnabled = true;
        }

        private void CreateSuggestedRules_Click(object sender, RoutedEventArgs e)
        {
            int addedCount = 0;
            int replacedCount = 0;

            foreach (var match in _suggestedMatches)
            {
                // Remove existing rule for this exact condition to avoid duplicates
                int removed = _config.Rules.RemoveAll(r => r.Weather == match.Weather && r.TimePeriod == match.TimePeriod);
                if (removed > 0) replacedCount++;
                
                _config.Rules.Add(new WallpaperRule { FileName = match.BestFileName, Weather = match.Weather, TimePeriod = match.TimePeriod });
                addedCount++;
            }

            if (addedCount > 0)
            {
                SaveConfig();
                RefreshRulesList();
                _ = ApplyRuleBasedWallpaperAsync();
                System.Windows.MessageBox.Show($"Successfully applied {addedCount} rules.\n{replacedCount} existing overlapping rules were replaced to prevent duplicates.", "Suggestions Accepted", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            
            _suggestedMatches.Clear();
            CreateSuggestedRulesBtn.IsEnabled = false;
            AITagResultsPanel.Children.Clear();
            AITagResultsPanel.Children.Add(new TextBlock { Text = "Rules updated. Ready for next analysis.", Foreground = new SolidColorBrush(Colors.Gray), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0,100,0,0) });
        }

        private void TestRules_Click(object sender, RoutedEventArgs e)
        {
            _ = ApplyRuleBasedWallpaperAsync();
        }

        private async Task ApplyRuleBasedWallpaperAsync(bool silent = false)
        {
            if (_isPaused) return;
            string timePeriod = GetCurrentTimePeriod();
            var rule = _config.Rules.FirstOrDefault(r => r.Weather == _currentWeatherCategory && r.TimePeriod == timePeriod);

            if (rule != null && rule.FileName != _currentAppliedWallpaper && !string.IsNullOrEmpty(_config.WallpaperFolderPath))
            {
                await ApplyWallpaperAsync(Path.Combine(_config.WallpaperFolderPath, rule.FileName));
            }
            
            Dispatcher.Invoke(() => {
                StatusMatchedText.Text = rule != null ? $"Rule Match: {rule.FileName}" : "Rule Match: None";
            });
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        private const int SPI_SETDESKWALLPAPER = 20;
        private const int SPIF_UPDATEINIFILE = 0x01;
        private const int SPIF_SENDCHANGE = 0x02;

        private Task ApplyWallpaperAsync(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath)) return Task.CompletedTask;

            try {
                // PROFESSIONAL: Use native Windows transition logic. 
                // IDesktopWallpaper automatically handles cross-fading based on system settings.
                // This is non-intrusive, stays behind all windows, and never steals focus.
                if (_desktopWallpaper != null) 
                {
                    // Setting wallpaper for all monitors (null ID)
                    _desktopWallpaper.SetWallpaper(null, fullPath);
                }
                else 
                {
                    // Fallback to legacy API if COM fails
                    SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, fullPath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                }

                _currentAppliedWallpaper = Path.GetFileName(fullPath);
                Log($"Applied: {fullPath}");
            } catch (Exception ex) { Log($"Wallpaper Apply Error: {ex.Message}"); }

            return Task.CompletedTask;
        }

        private void StartWithWindows_Click(object sender, RoutedEventArgs e)
        {
            bool isChecked = StartWithWindowsCheck.IsChecked ?? false;
            _config.StartWithWindows = isChecked;
            SaveConfig();
            try {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)!;
                if (isChecked) {
                    string? exePath = Environment.ProcessPath;
                    if (exePath != null) key.SetValue("WeatherWall", $"\"{exePath}\" --minimized");
                } else { key.DeleteValue("WeatherWall", false); }
            } catch (Exception ex) { Log($"Startup Error: {ex.Message}"); }
        }
    }

    public class WallpaperRule
    {
        public string Weather { get; set; } = "clear";
        public string TimePeriod { get; set; } = "morning";
        public string FileName { get; set; } = "";
    }

    public class AppConfig
    {
        public string WallpaperFolderPath { get; set; } = "";
        public bool StartWithWindows { get; set; } = false;
        public bool IsPaused { get; set; } = false;
        public List<WallpaperRule> Rules { get; set; } = new();

        public double Latitude { get; set; } = 0;
        public double Longitude { get; set; } = 0;
        public string LocationName { get; set; } = "Unknown";
        public bool AutoLocation { get; set; } = true;

        public string OpenWeatherMapKey { get; set; } = "";
        public string WeatherApiKey { get; set; } = "";
        public string TomorrowIoKey { get; set; } = "";
        public string AccuWeatherKey { get; set; } = "";

        public string ManualOverrideWeather { get; set; } = "";
        public DateTime? ManualOverrideExpires { get; set; }
    }
}