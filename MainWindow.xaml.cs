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

        private BitmapSource? _thumbnail;
        public BitmapSource? Thumbnail 
        { 
            get 
            {
                if (_thumbnail == null && !string.IsNullOrEmpty(FullPath)) 
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

            _ = InitialSyncAsync();
            
            StartWithWindowsCheck.IsChecked = _config.StartWithWindows;
            AutoLocationCheck.IsChecked = _config.AutoLocation;
            LocationInput.Text = _config.LocationName;
            
            this.Closing += MainWindow_Closing;
            this.StateChanged += MainWindow_StateChanged;
            this.SourceInitialized += (s, e) => UpdateTitleBarTheme();

            ApplyTheme();

            if (_config.AutoLocation) _ = DetectLocationAsync();

            _ = CheckFirstLaunchAsync();
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
                var response = await _httpClient.GetStringAsync("http://ip-api.com/json/");
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                if (root.GetProperty("status").GetString() == "success")
                {
                    _config.Latitude = root.GetProperty("lat").GetDouble();
                    _config.Longitude = root.GetProperty("lon").GetDouble();
                    _config.LocationName = root.GetProperty("city").GetString() ?? "Unknown";
                    SaveConfig();
                    await UpdateWeatherAsync();
                }
            }
            catch (Exception ex) { Log($"Location Detection Error: {ex.Message}"); }
        }

        private void ApplyTheme()
        {
            // PROFESSIONAL: Hardcoded Dark Theme for brand consistency (matches HD logo)
            var res = this.Resources;
            res["WindowBackgroundBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 24, 39));
            res["CardBackgroundBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(31, 41, 55));
            res["PrimaryTextBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(249, 250, 251));
            res["SecondaryTextBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(156, 163, 175));
            res["BorderBrush"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(55, 65, 81));
            res["WeatherChipBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59));
            res["WeatherChipForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
            res["TimeChipBackground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 30, 20));
            res["TimeChipForeground"] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 150, 100));
            
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
                int captionColor = 0x271811; // BGR format for #111827
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
                var iconUri = new Uri("pack://application:,,,/icon.ico");
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
                contextMenu.Items.Add("Sync Now", null, async (s, e) => await AutoSyncAsync());
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
            AutoSyncLabel.Foreground = _isPaused ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(128, 128, 128)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
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
                // Reload thumbnails when window is shown
                var files = FileListBox.ItemsSource;
                FileListBox.ItemsSource = null;
                FileListBox.ItemsSource = files;

                var gallery = RuleWallpaperGallery.ItemsSource;
                RuleWallpaperGallery.ItemsSource = null;
                RuleWallpaperGallery.ItemsSource = gallery;

                RefreshRulesList();

                // Ensure empty states are correctly shown
                NoWallpapersState.Visibility = (FileListBox.ItemsSource == null || !((IEnumerable<WallpaperItem>)FileListBox.ItemsSource).Any()) ? Visibility.Visible : Visibility.Collapsed;
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
                // Clear thumbnails from objects to allow GC to reclaim them
                if (FileListBox.ItemsSource is IEnumerable<WallpaperItem> items)
                    foreach (var item in items) item.ClearThumbnail();
                
                if (RuleWallpaperGallery.ItemsSource is IEnumerable<WallpaperItem> gItems)
                    foreach (var item in gItems) item.ClearThumbnail();

                if (ActiveRulesListBox.ItemsSource is IEnumerable<RuleItem> rItems)
                    foreach (var item in rItems) item.ClearThumbnail();

                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                
                // Clear working set to release memory back to OS
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

        private async Task AutoSyncAsync()
        {
            if (_isPaused) return;
            await UpdateWeatherAsync();
            await ApplyRuleBasedWallpaperAsync();
            Dispatcher.Invoke(() => StatusLastUpdateText.Text = $"Last Sync: {DateTime.Now:HH:mm:ss}");
        }

        private async Task UpdateWeatherAsync()
        {
            try
            {
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={_config.Latitude}&longitude={_config.Longitude}&current=weather_code,temperature_2m&daily=sunrise,sunset&timezone=auto";

                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                
                var current = root.GetProperty("current");
                int code = current.GetProperty("weather_code").GetInt32();
                double temp = current.GetProperty("temperature_2m").GetDouble();
                
                var daily = root.GetProperty("daily");
                string sunriseStr = daily.GetProperty("sunrise").EnumerateArray().First().GetString() ?? "";
                string sunsetStr = daily.GetProperty("sunset").EnumerateArray().First().GetString() ?? "";
                
                _sunrise = DateTime.Parse(sunriseStr);
                _sunset = DateTime.Parse(sunsetStr);
                _currentTimeZone = root.GetProperty("timezone").GetString() ?? "UTC";

                var (status, icon, category) = MapWeatherCode(code);
                _currentWeatherCategory = category;
                
                Dispatcher.Invoke(() => {
                    WeatherStatusText.Text = $"{status} ({temp:0}°C)";
                    WeatherIconText.Text = icon;
                    
                    string timePeriod = GetCurrentTimePeriod();
                    StatusConditionText.Text = $"{_config.LocationName.ToUpper()} · {status.ToUpper()} · {timePeriod.ToUpper()}";
                    
                    // Update the detailed status bar context
                    StatusMatchedText.Text = $"{_config.LocationName} · {status} · {timePeriod} · Sunset {_sunset?.ToString("h:mm tt")}";
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => {
                    WeatherStatusText.Text = "Offline";
                    WeatherIconText.Text = "⚠️";
                });
                Log($"Weather Fetch Error: {ex.Message}");
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
            return code switch
            {
                0 => ("Clear", "☀️", "clear"),
                1 or 2 => ("Partly Cloudy", "⛅", "partly_cloudy"),
                3 => ("Overcast", "☁️", "overcast"),
                45 or 48 => ("Foggy", "🌫️", "foggy"),
                51 or 53 or 55 => ("Drizzle", "🌦️", "drizzle"),
                61 or 63 or 65 or 80 or 81 or 82 => ("Rainy", "🌧️", "rainy"),
                71 or 73 or 75 or 77 or 85 or 86 => ("Snowy", "❄️", "snowy"),
                95 or 96 or 99 => ("Thunderstorm", "⛈️", "thunderstorm"),
                _ => ("Unknown", "❓", "unknown")
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
                // Use Open-Meteo Geocoding API
                string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=1&language=en&format=json";
                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                var results = doc.RootElement.GetProperty("results");
                if (results.GetArrayLength() > 0)
                {
                    var first = results.EnumerateArray().First();
                    _config.Latitude = first.GetProperty("latitude").GetDouble();
                    _config.Longitude = first.GetProperty("longitude").GetDouble();
                    _config.LocationName = first.GetProperty("name").GetString() ?? query;
                    _config.AutoLocation = false;
                    AutoLocationCheck.IsChecked = false;
                    
                    SaveConfig();
                    await UpdateWeatherAsync();
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
            if (!Directory.Exists(path)) return;
            string[] extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            try
            {
                var files = Directory.EnumerateFiles(path)
                    .Where(file => extensions.Contains(Path.GetExtension(file).ToLower()))
                    .ToList();
                
                var items = new List<WallpaperItem>();
                foreach (var file in files)
                {
                    // MEMORY: Only store path, do NOT create thumbnail yet
                    items.Add(new WallpaperItem { FullPath = file });
                }
                
                Dispatcher.Invoke(() => {
                    FileListBox.ItemsSource = items;
                    RuleWallpaperGallery.ItemsSource = items.ToList();
                    NoWallpapersState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    RefreshRulesList();
                });
                GC.Collect();
            }
            catch (Exception ex) { Log($"Scan Error: {ex.Message}"); }
        }

        private void FileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

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
    }
}