# WeatherWall

WeatherWall is a sophisticated Windows utility that automatically synchronizes your desktop wallpaper with real-time weather conditions and the time of day.

## 🎯 For End Users

### Download
Pre-built releases are available on the [Releases](../../releases) page.

### Features
- **Adaptive Environments**: Auto-switches wallpapers based on weather (clear, cloudy, rainy, foggy, stormy)
- **Time-Aware**: Morning, afternoon, evening, and night themes
- **Smooth Transitions**: Flicker-free fullscreen fade transitions
- **Modern UI**: Windows 11-inspired interface with Light/Dark mode
- **Lightweight**: Minimal system resources, runs quietly in system tray
- **Auto-Start**: Optional background launching on startup

### System Requirements
- Windows 10 or later
- .NET Runtime 8.0 (included in release downloads)
- Active internet connection for weather updates

### First Launch

WeatherWall starts completely clean on first launch with zero pre-configured data:

1. **Welcome Dialog**: You'll be prompted to select your wallpaper folder
   - Choose a folder containing your wallpaper images (PNG, JPG, etc.)
   - No wallpaper folder is pre-selected

2. **Default Settings**:
   - No automation rules configured
   - Location auto-detection enabled (uses your IP location, respects privacy)
   - App starts in normal mode (not minimized)
   - Does NOT start with Windows (you choose)

3. **Configuration**:
   - Settings are saved to `config.json` in the app folder
   - This file is created after first launch
   - You can manually edit it for advanced configuration
   - See [Configuration](#configuration) section below

### Configuration

All settings are stored in `config.json`:

```json
{
  "WallpaperFolderPath": "C:\\Users\\YourName\\Pictures\\Wallpapers",
  "StartWithWindows": false,
  "IsPaused": false,
  "Rules": [
    {
      "Weather": "clear",
      "TimePeriod": "morning",
      "FileName": "sunny-morning.png"
    }
  ],
  "Latitude": 0.0,
  "Longitude": 0.0,
  "LocationName": "Auto-detect",
  "AutoLocation": true
}
```

**Settings Explained:**
- `WallpaperFolderPath`: Folder containing your wallpaper images
- `StartWithWindows`: Auto-launch app on system startup
- `IsPaused`: Pause wallpaper updates temporarily
- `Rules`: Array of weather + time of day rules
- `Latitude/Longitude`: Your location for weather data
- `LocationName`: Display name for your location
- `AutoLocation`: Auto-detect location vs. manual

### Weather & Time Rules

Rules determine which wallpaper displays for specific conditions:

**Weather Types:** `clear`, `cloudy`, `rainy`, `foggy`, `stormy`  
**Time Periods:** `morning`, `afternoon`, `evening`, `night`

Example rule:
```json
{
  "Weather": "clear",
  "TimePeriod": "morning",
  "FileName": "sunny-morning.png"
}
```

Create rules in the UI or manually add to `config.json`.

### Privacy & Security

✅ **What we protect:**
- Your machine paths are kept private
- No personal data collection
- No cloud storage of settings
- All data stays on your computer

See [PRIVACY.md](PRIVACY.md) for complete privacy details.

---

## 💻 For Developers

### Building From Source

**Prerequisites:**
- Visual Studio 2022 or Visual Studio Code with C# extension
- .NET 8.0 SDK
- Windows 10/11

**Build Steps:**
```bash
git clone https://github.com/MayankArambhi/WeatherWall.git
cd WeatherWall
dotnet build -c Release
```

**Output:** Built executable will be in `bin/Release/net8.0-windows/win-x64/`

### Project Structure
```
WeatherWall/
├── App.xaml                 # Application configuration
├── MainWindow.xaml          # Main UI
├── SplashWindow.xaml        # Splash screen
├── config.json              # User configuration
├── WeatherWall.csproj       # Project file
├── WeatherWall.sln          # Solution file
└── WW/                      # Resources (icons, images)
```

### Technologies
- **WPF** (Windows Presentation Foundation) for UI
- **.NET 8** (Windows Desktop Targeted)
- **Windows Forms** for system tray integration

### Creating a Release Build
```bash
dotnet publish -c Release -r win-x64 --self-contained
```

---

## 📝 License

[Add your license here]

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 👤 Authors

**Mayankarambhi**

---

*For feature requests and bug reports, please use the [Issues](../../issues) tab.*
