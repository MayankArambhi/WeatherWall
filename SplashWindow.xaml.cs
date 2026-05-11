using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace WeatherWall
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            this.Loaded += SplashWindow_Loaded;
        }

        private void SplashWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (Resources["FadeIn"] is Storyboard fadeIn)
            {
                fadeIn.Begin(this);
            }
        }

        public async Task FadeOutAndClose()
        {
            if (Resources["FadeOut"] is Storyboard fadeOut)
            {
                fadeOut.Begin(this);
                await Task.Delay(400); // Wait for fade out duration
            }
            this.Close();
        }
    }
}
