using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace WeatherWall
{
    public class ProviderWeatherResult
    {
        public string ProviderName { get; set; } = "";
        public bool Success { get; set; } = false;
        public string ErrorMessage { get; set; } = "";

        // Raw metrics
        public string RawCode { get; set; } = "";
        public string RawDescription { get; set; } = "";
        public double? CloudCover { get; set; } // 0-100%
        public double? Precipitation { get; set; } // mm
        public double? RainProbability { get; set; } // % if available
        public double? Temperature { get; set; } // °C
        public DateTime? ObservationTime { get; set; }

        public DateTime? Sunrise { get; set; }
        public DateTime? Sunset { get; set; }
        public string Timezone { get; set; } = "UTC";

        // Interpreted category
        public string InterpretedCondition { get; set; } = "unknown";
    }

    public interface IWeatherProvider
    {
        string Name { get; }
        bool RequiresApiKey { get; }
        Task<ProviderWeatherResult> GetWeatherAsync(HttpClient httpClient, double latitude, double longitude, string apiKey = "");
    }

    public static class WeatherMapper
    {
        public static string Interpret(
            double? cloudCover,
            double? precipitation,
            bool isThunderstormCode,
            bool isSnowCode,
            bool isFogCode,
            string providerName)
        {
            if (isThunderstormCode)
                return "thunderstorm";

            if (isSnowCode)
                return "snowy";

            if (isFogCode)
                return "foggy";

            // Do not classify rainy unless actual current precipitation exists (> 0 mm)
            if (precipitation.HasValue && precipitation.Value > 0)
                return "rainy";

            if (cloudCover.HasValue)
            {
                double cc = cloudCover.Value;
                if (cc <= 25) return "clear";
                if (cc <= 60) return "partly_cloudy";
                if (cc <= 85) return "cloudy";
                return "overcast";
            }

            return "clear";
        }

        public static string GetFriendlyName(string category)
        {
            return category switch
            {
                "clear" => "Clear",
                "partly_cloudy" => "Partly Cloudy",
                "cloudy" => "Cloudy",
                "overcast" => "Overcast",
                "rainy" => "Rainy",
                "thunderstorm" => "Thunderstorm",
                "foggy" => "Foggy",
                "snowy" => "Snowy",
                _ => "Clear"
            };
        }

        public static string GetIcon(string category)
        {
            return category switch
            {
                "clear" => "☀️",
                "partly_cloudy" => "⛅",
                "cloudy" => "☁️",
                "overcast" => "☁️",
                "rainy" => "🌧️",
                "thunderstorm" => "⛈️",
                "foggy" => "🌫️",
                "snowy" => "❄️",
                _ => "☁️"
            };
        }
    }

    public class OpenMeteoProvider : IWeatherProvider
    {
        public string Name => "Open-Meteo";
        public bool RequiresApiKey => false;

        public async Task<ProviderWeatherResult> GetWeatherAsync(HttpClient httpClient, double latitude, double longitude, string apiKey = "")
        {
            var result = new ProviderWeatherResult { ProviderName = Name };
            try
            {
                string url = $"https://api.open-meteo.com/v1/forecast?latitude={latitude:F4}&longitude={longitude:F4}&current=weather_code,temperature_2m,is_day,cloud_cover,precipitation&daily=sunrise,sunset&timezone=auto";
                var response = await httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                if (root.TryGetProperty("current", out var current))
                {
                    int code = current.GetProperty("weather_code").GetInt32();
                    double temp = current.GetProperty("temperature_2m").GetDouble();
                    double cloud = current.GetProperty("cloud_cover").GetDouble();
                    double precip = current.GetProperty("precipitation").GetDouble();
                    string timeStr = current.TryGetProperty("time", out var timeProp) ? timeProp.GetString() ?? "" : "";

                    result.RawCode = code.ToString();
                    result.RawDescription = GetWmoCodeDescription(code);
                    result.CloudCover = cloud;
                    result.Precipitation = precip;
                    result.Temperature = temp;
                    result.Success = true;
                    if (DateTime.TryParse(timeStr, out var parsedTime))
                    {
                        result.ObservationTime = parsedTime;
                    }
                    else
                    {
                        result.ObservationTime = DateTime.Now;
                    }

                    bool isThunder = code is 95 or 96 or 99;
                    bool isSnow = code is 71 or 73 or 75 or 77 or 85 or 86;
                    bool isFog = code is 45 or 48;

                    result.InterpretedCondition = WeatherMapper.Interpret(cloud, precip, isThunder, isSnow, isFog, Name);

                    // Parse Sunrise, Sunset and Timezone
                    result.Timezone = root.GetProperty("timezone").GetString() ?? "UTC";
                    if (root.TryGetProperty("daily", out var daily))
                    {
                        if (daily.TryGetProperty("sunrise", out var sunriseArr) && sunriseArr.GetArrayLength() > 0)
                        {
                            string sunriseStr = sunriseArr.EnumerateArray().First().GetString() ?? "";
                            if (DateTime.TryParse(sunriseStr, out var sr)) result.Sunrise = sr;
                        }
                        if (daily.TryGetProperty("sunset", out var sunsetArr) && sunsetArr.GetArrayLength() > 0)
                        {
                            string sunsetStr = sunsetArr.EnumerateArray().First().GetString() ?? "";
                            if (DateTime.TryParse(sunsetStr, out var ss)) result.Sunset = ss;
                        }
                    }
                }
                else
                {
                    result.ErrorMessage = "No 'current' element found in response.";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        private string GetWmoCodeDescription(int code)
        {
            return code switch
            {
                0 => "Clear sky",
                1 => "Mainly clear",
                2 => "Partly cloudy",
                3 => "Overcast",
                45 => "Fog",
                48 => "Depositing rime fog",
                51 => "Light drizzle",
                53 => "Moderate drizzle",
                55 => "Dense drizzle",
                56 => "Light freezing drizzle",
                57 => "Dense freezing drizzle",
                61 => "Slight rain",
                63 => "Moderate rain",
                65 => "Heavy rain",
                66 => "Light freezing rain",
                67 => "Heavy freezing rain",
                71 => "Slight snow fall",
                73 => "Moderate snow fall",
                75 => "Heavy snow fall",
                77 => "Snow grains",
                80 => "Slight rain showers",
                81 => "Moderate rain showers",
                82 => "Violent rain showers",
                85 => "Slight snow showers",
                86 => "Heavy snow showers",
                95 => "Thunderstorm",
                96 => "Thunderstorm with slight hail",
                99 => "Thunderstorm with heavy hail",
                _ => "Unknown code"
            };
        }
    }

    public class MetNorwayProvider : IWeatherProvider
    {
        public string Name => "MET Norway (Yr.no)";
        public bool RequiresApiKey => false;

        public async Task<ProviderWeatherResult> GetWeatherAsync(HttpClient httpClient, double latitude, double longitude, string apiKey = "")
        {
            var result = new ProviderWeatherResult { ProviderName = Name };
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={latitude:F4}&lon={longitude:F4}");
                request.Headers.UserAgent.ParseAdd("WeatherWall/1.1.0 (contact: info@weatherwall.com)");

                var responseMessage = await httpClient.SendAsync(request);
                responseMessage.EnsureSuccessStatusCode();
                var response = await responseMessage.Content.ReadAsStringAsync();

                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                if (root.TryGetProperty("properties", out var props) && props.TryGetProperty("timeseries", out var timeseries) && timeseries.GetArrayLength() > 0)
                {
                    var currentPoint = timeseries.EnumerateArray().First();
                    string timeStr = currentPoint.TryGetProperty("time", out var timeProp) ? timeProp.GetString() ?? "" : "";

                    var data = currentPoint.GetProperty("data");
                    var instantDetails = data.GetProperty("instant").GetProperty("details");

                    double temp = instantDetails.GetProperty("air_temperature").GetDouble();
                    double cloud = instantDetails.GetProperty("cloud_area_fraction").GetDouble();

                    double precip = 0;
                    if (data.TryGetProperty("next_1_hours", out var next1h))
                    {
                        if (next1h.TryGetProperty("details", out var next1hDetails) && next1hDetails.TryGetProperty("precipitation_amount", out var precipProp))
                        {
                            precip = precipProp.GetDouble();
                        }
                    }

                    string symbolCode = "";
                    if (data.TryGetProperty("next_1_hours", out var next1hObj))
                    {
                        if (next1hObj.TryGetProperty("summary", out var summary) && summary.TryGetProperty("symbol_code", out var symbolProp))
                        {
                            symbolCode = symbolProp.GetString() ?? "";
                        }
                    }

                    result.RawCode = symbolCode;
                    result.RawDescription = symbolCode.Replace("_", " ");
                    result.CloudCover = cloud;
                    result.Precipitation = precip;
                    result.Temperature = temp;
                    result.Success = true;
                    if (DateTime.TryParse(timeStr, out var parsedTime))
                    {
                        result.ObservationTime = parsedTime.ToLocalTime();
                    }
                    else
                    {
                        result.ObservationTime = DateTime.Now;
                    }

                    // For MET Norway forecast, only classify as thunder if symbol contains thunder AND there is actual precipitation.
                    bool isThunder = symbolCode.Contains("thunder") && precip > 0;
                    bool isSnow = symbolCode.Contains("snow") || symbolCode.Contains("sleet");
                    bool isFog = symbolCode.Contains("fog");

                    result.InterpretedCondition = WeatherMapper.Interpret(cloud, precip, isThunder, isSnow, isFog, Name);
                }
                else
                {
                    result.ErrorMessage = "Unexpected API JSON structure.";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return result;
        }
    }

    public class OpenWeatherMapProvider : IWeatherProvider
    {
        public string Name => "OpenWeatherMap";
        public bool RequiresApiKey => true;

        public async Task<ProviderWeatherResult> GetWeatherAsync(HttpClient httpClient, double latitude, double longitude, string apiKey = "")
        {
            var result = new ProviderWeatherResult { ProviderName = Name };
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.ErrorMessage = "API Key not configured.";
                return result;
            }

            try
            {
                string url = $"https://api.openweathermap.org/data/2.5/weather?lat={latitude:F4}&lon={longitude:F4}&appid={apiKey}&units=metric";
                var response = await httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                double temp = root.GetProperty("main").GetProperty("temp").GetDouble();
                double cloud = root.GetProperty("clouds").GetProperty("all").GetDouble();

                double precip = 0;
                if (root.TryGetProperty("rain", out var rainObj))
                {
                    if (rainObj.TryGetProperty("1h", out var rain1h)) precip += rain1h.GetDouble();
                }
                if (root.TryGetProperty("snow", out var snowObj))
                {
                    if (snowObj.TryGetProperty("1h", out var snow1h)) precip += snow1h.GetDouble();
                }

                var weatherArray = root.GetProperty("weather");
                int code = 0;
                string desc = "";
                if (weatherArray.GetArrayLength() > 0)
                {
                    var weatherFirst = weatherArray.EnumerateArray().First();
                    code = weatherFirst.GetProperty("id").GetInt32();
                    desc = weatherFirst.GetProperty("description").GetString() ?? "";
                }

                long dt = root.GetProperty("dt").GetInt64();
                result.RawCode = code.ToString();
                result.RawDescription = desc;
                result.CloudCover = cloud;
                result.Precipitation = precip;
                result.Temperature = temp;
                result.Success = true;
                result.ObservationTime = DateTimeOffset.FromUnixTimeSeconds(dt).LocalDateTime;

                bool isThunder = (code >= 200 && code < 300);
                bool isSnow = (code >= 600 && code < 700);
                bool isFog = (code == 701 || code == 711 || code == 721 || code == 741);

                result.InterpretedCondition = WeatherMapper.Interpret(cloud, precip, isThunder, isSnow, isFog, Name);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return result;
        }
    }

    public class WeatherApiProvider : IWeatherProvider
    {
        public string Name => "WeatherAPI";
        public bool RequiresApiKey => true;

        public async Task<ProviderWeatherResult> GetWeatherAsync(HttpClient httpClient, double latitude, double longitude, string apiKey = "")
        {
            var result = new ProviderWeatherResult { ProviderName = Name };
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.ErrorMessage = "API Key not configured.";
                return result;
            }

            try
            {
                string url = $"https://api.weatherapi.com/v1/current.json?key={apiKey}&q={latitude:F4},{longitude:F4}";
                var response = await httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                var current = root.GetProperty("current");
                double temp = current.GetProperty("temp_c").GetDouble();
                double cloud = current.GetProperty("cloud").GetDouble();
                double precip = current.GetProperty("precip_mm").GetDouble();

                var condition = current.GetProperty("condition");
                int code = condition.GetProperty("code").GetInt32();
                string desc = condition.GetProperty("text").GetString() ?? "";

                string updateTimeStr = current.GetProperty("last_updated").GetString() ?? "";

                result.RawCode = code.ToString();
                result.RawDescription = desc;
                result.CloudCover = cloud;
                result.Precipitation = precip;
                result.Temperature = temp;
                result.Success = true;
                if (DateTime.TryParse(updateTimeStr, out var parsedTime))
                {
                    result.ObservationTime = parsedTime;
                }
                else
                {
                    result.ObservationTime = DateTime.Now;
                }

                bool isThunder = code is 1087 or 1273 or 1276 or 1279 or 1282;
                bool isSnow = code is 1114 or 1117 or 1210 or 1213 or 1216 or 1219 or 1222 or 1225 or 1255 or 1258 or 1261 or 1264 or 1279 or 1282;
                bool isFog = code is 1135 or 1147;

                result.InterpretedCondition = WeatherMapper.Interpret(cloud, precip, isThunder, isSnow, isFog, Name);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return result;
        }
    }

    public class TomorrowIoProvider : IWeatherProvider
    {
        public string Name => "Tomorrow.io";
        public bool RequiresApiKey => true;

        public async Task<ProviderWeatherResult> GetWeatherAsync(HttpClient httpClient, double latitude, double longitude, string apiKey = "")
        {
            var result = new ProviderWeatherResult { ProviderName = Name };
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.ErrorMessage = "API Key not configured.";
                return result;
            }

            try
            {
                string url = $"https://api.tomorrow.io/v4/weather/realtime?location={latitude:F4},{longitude:F4}&apikey={apiKey}";
                var response = await httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                var data = root.GetProperty("data");
                var values = data.GetProperty("values");

                double temp = values.GetProperty("temperature").GetDouble();
                double cloud = values.GetProperty("cloudCover").GetDouble();
                double precip = values.GetProperty("precipitationIntensity").GetDouble();
                
                double? rainProb = null;
                if (values.TryGetProperty("precipitationProbability", out var probProp))
                {
                    rainProb = probProp.GetDouble();
                }

                int code = values.GetProperty("weatherCode").GetInt32();
                string timeStr = data.GetProperty("time").GetString() ?? "";

                result.RawCode = code.ToString();
                result.RawDescription = GetTomorrowIoCodeDescription(code);
                result.CloudCover = cloud;
                result.Precipitation = precip;
                result.RainProbability = rainProb;
                result.Temperature = temp;
                result.Success = true;
                if (DateTime.TryParse(timeStr, out var parsedTime))
                {
                    result.ObservationTime = parsedTime.ToLocalTime();
                }
                else
                {
                    result.ObservationTime = DateTime.Now;
                }

                bool isThunder = code >= 8000 && code <= 8999;
                bool isSnow = code >= 5000 && code <= 5999;
                bool isFog = code >= 2000 && code <= 2999;

                result.InterpretedCondition = WeatherMapper.Interpret(cloud, precip, isThunder, isSnow, isFog, Name);
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        private string GetTomorrowIoCodeDescription(int code)
        {
            return code switch
            {
                0 => "Unknown",
                1000 => "Clear, Sunny",
                1100 => "Mostly Clear",
                1101 => "Partly Cloudy",
                1102 => "Mostly Cloudy",
                1001 => "Cloudy",
                2000 => "Fog",
                2100 => "Light Fog",
                3000 => "Light Wind",
                3001 => "Wind",
                3002 => "Strong Wind",
                4000 => "Drizzle",
                4001 => "Rain",
                4200 => "Light Rain",
                4201 => "Heavy Rain",
                5000 => "Snow",
                5001 => "Flurries",
                5100 => "Light Snow",
                5101 => "Heavy Snow",
                6000 => "Freezing Drizzle",
                6001 => "Freezing Rain",
                6200 => "Light Freezing Rain",
                6201 => "Heavy Freezing Rain",
                7000 => "Ice Pellets",
                7101 => "Heavy Ice Pellets",
                7102 => "Light Ice Pellets",
                8000 => "Thunderstorm",
                _ => "Unknown code"
            };
        }
    }

    public class AccuWeatherProvider : IWeatherProvider
    {
        public string Name => "AccuWeather";
        public bool RequiresApiKey => true;

        public async Task<ProviderWeatherResult> GetWeatherAsync(HttpClient httpClient, double latitude, double longitude, string apiKey = "")
        {
            var result = new ProviderWeatherResult { ProviderName = Name };
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                result.ErrorMessage = "API Key not configured.";
                return result;
            }

            try
            {
                string searchUrl = $"http://dataservice.accuweather.com/locations/v1/cities/geoposition/search?apikey={apiKey}&q={latitude:F4},{longitude:F4}";
                var searchResponse = await httpClient.GetStringAsync(searchUrl);
                using var searchDoc = JsonDocument.Parse(searchResponse);
                var locationKey = searchDoc.RootElement.GetProperty("Key").GetString();
                if (string.IsNullOrEmpty(locationKey))
                {
                    result.ErrorMessage = "Failed to resolve AccuWeather location key.";
                    return result;
                }

                string currentUrl = $"http://dataservice.accuweather.com/currentconditions/v1/{locationKey}?apikey={apiKey}&details=true";
                var currentResponse = await httpClient.GetStringAsync(currentUrl);
                using var currentDoc = JsonDocument.Parse(currentResponse);
                var root = currentDoc.RootElement;
                if (root.GetArrayLength() > 0)
                {
                    var data = root.EnumerateArray().First();
                    double temp = data.GetProperty("Temperature").GetProperty("Metric").GetProperty("Value").GetDouble();
                    double cloud = data.GetProperty("CloudCover").GetDouble();

                    double precip = 0;
                    if (data.TryGetProperty("PrecipitationSummary", out var precipSummary))
                    {
                        if (precipSummary.TryGetProperty("PastHour", out var pastHour) && pastHour.TryGetProperty("Metric", out var metric))
                        {
                            precip = metric.GetProperty("Value").GetDouble();
                        }
                    }

                    int weatherIcon = data.GetProperty("WeatherIcon").GetInt32();
                    string weatherText = data.GetProperty("WeatherText").GetString() ?? "";
                    string obsTimeStr = data.GetProperty("LocalObservationDateTime").GetString() ?? "";

                    result.RawCode = weatherIcon.ToString();
                    result.RawDescription = weatherText;
                    result.CloudCover = cloud;
                    result.Precipitation = precip;
                    result.Temperature = temp;
                    result.Success = true;
                    if (DateTime.TryParse(obsTimeStr, out var parsedTime))
                    {
                        result.ObservationTime = parsedTime;
                    }
                    else
                    {
                        result.ObservationTime = DateTime.Now;
                    }

                    bool isThunder = weatherIcon is 15 or 16 or 17 or 41 or 42;
                    bool isSnow = weatherIcon is 22 or 23 or 24 or 25 or 26 or 29 or 44;
                    bool isFog = weatherIcon == 11;

                    result.InterpretedCondition = WeatherMapper.Interpret(cloud, precip, isThunder, isSnow, isFog, Name);
                }
                else
                {
                    result.ErrorMessage = "No data returned in current conditions.";
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            return result;
        }
    }
}
