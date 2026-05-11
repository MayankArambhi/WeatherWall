using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace WeatherWall
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;

        protected override async void OnStartup(StartupEventArgs e)
        {
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

            // Ensure splash stays for at least 1 second
            var elapsed = DateTime.Now - startTime;
            if (elapsed.TotalMilliseconds < 1000)
            {
                await Task.Delay(1000 - (int)elapsed.TotalMilliseconds);
            }

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

            // Fade out and close splash
            await splash.FadeOutAndClose();
        }
    }
}
