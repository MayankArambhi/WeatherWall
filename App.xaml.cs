using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Windows;

namespace WeatherWall
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Global exception logging for diagnostics
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
            {
                try { LogException(ev.ExceptionObject as Exception, "AppDomain"); LogExceptionToTemp(ev.ExceptionObject as Exception, "AppDomain"); } catch { }
            };

            this.DispatcherUnhandledException += (s, ev) =>
            {
                try { LogException(ev.Exception, "Dispatcher"); LogExceptionToTemp(ev.Exception, "Dispatcher"); } catch { }
                // let default behavior continue
            };

            void LogException(Exception? ex, string source)
            {
                try
                {
                    var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
                    var text = $"[{DateTime.Now:O}] {source}: {ex?.ToString() ?? "(null)"}\r\n";
                    File.AppendAllText(path, text);
                }
                catch { }
            }
                // Also log to temp path to ensure visibility even if base dir is locked
                void LogExceptionToTemp(Exception? ex, string source)
                {
                    try
                    {
                        var tmp = Path.Combine(Path.GetTempPath(), "WeatherWall_crash_log.txt");
                        var text = $"[{DateTime.Now:O}] {source}: {ex?.ToString() ?? "(null)"}\r\n";
                        File.AppendAllText(tmp, text);
                    }
                    catch { }
                }
            const string appName = "WeatherWall_SingleInstance_Mutex";
            _mutex = new Mutex(true, appName, out bool createdNew);

            if (!createdNew)
            {
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // Show splash screen immediately (guarded)
            SplashWindow? splash = null;
            try
            {
                splash = new SplashWindow();
                splash.Show();
            }
            catch (Exception ex)
            {
                try { LogException(ex, "SplashCreation"); LogExceptionToTemp(ex, "SplashCreation"); } catch { }
                System.Windows.MessageBox.Show($"Failed to create splash window:\n{ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // Diagnostic: attempt to load Icons.xaml separately to capture detailed errors early
            try
            {
                var icons = new ResourceDictionary();
                icons.Source = new Uri("Themes/Icons.xaml", UriKind.Relative);
            }
            catch (Exception ex)
            {
                LogException(ex, "IconsLoadDiagnostic"); LogExceptionToTemp(ex, "IconsLoadDiagnostic");
            }

            base.OnStartup(e);

            // Initialize MainWindow in background
            var startTime = DateTime.Now;
            MainWindow? mainWindow = null;
            try
            {
                mainWindow = new MainWindow();
            }
            catch (Exception ex)
            {
                try { LogException(ex, "MainWindowCreation"); LogExceptionToTemp(ex, "MainWindowCreation"); } catch { }
                System.Windows.MessageBox.Show($"Failed to create main window:\n{ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                try { if (splash != null) { await splash.FadeOutAndClose(); } } catch { }
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // Ensure splash stays for at least 3 seconds
            var elapsed = DateTime.Now - startTime;
            if (elapsed.TotalMilliseconds < 3000)
            {
                await Task.Delay(3000 - (int)elapsed.TotalMilliseconds);
            }

            // Fade out and close splash FIRST
            await splash.FadeOutAndClose();

            // Small extra delay for a cleaner transition (3.1 - 3.2s total)
            await Task.Delay(150);

            // Check if we should start minimized
            if (e.Args.Contains("--minimized"))
            {
                mainWindow.WindowState = WindowState.Minimized;
                mainWindow.Hide();
            }
            else
            {
                try
                {
                    mainWindow.Show();
                    mainWindow.Activate();
                }
                catch (Exception ex)
                {
                    try { LogException(ex, "MainWindowShow"); LogExceptionToTemp(ex, "MainWindowShow"); } catch { }
                    System.Windows.MessageBox.Show($"Failed to show main window:\n{ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    try { if (splash != null) { await splash.FadeOutAndClose(); } } catch { }
                    System.Windows.Application.Current.Shutdown();
                    return;
                }
            }

            // Reset shutdown mode to normal now that windows are managed
            System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
    }
}
