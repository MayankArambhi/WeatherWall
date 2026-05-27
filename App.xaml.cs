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
                try { LogException(ev.ExceptionObject as Exception, "AppDomain"); } catch { }
            };

            this.DispatcherUnhandledException += (s, ev) =>
            {
                try { LogException(ev.Exception, "Dispatcher"); } catch { }
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
            const string appName = "WeatherWall_SingleInstance_Mutex";
            _mutex = new Mutex(true, appName, out bool createdNew);

            if (!createdNew)
            {
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // Show splash screen immediately
            var splash = new SplashWindow();
            splash.Show();

            base.OnStartup(e);

            // Initialize MainWindow in background
            var startTime = DateTime.Now;
            var mainWindow = new MainWindow();

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
                mainWindow.Show();
                mainWindow.Activate();
            }

            // Reset shutdown mode to normal now that windows are managed
            System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
    }
}
