using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WeatherWall
{
    public class ConfidenceScore
    {
        public int Score { get; set; }
        public bool NeedsReview => Score < 65;
    }

    public class ImageTagResult
    {
        public string FileName { get; set; } = "";
        public List<string> Descriptors { get; set; } = new();
    }

    public class SuggestedRule
    {
        public string Weather { get; set; } = "clear";
        public string TimePeriod { get; set; } = "morning";
        public string BestFileName { get; set; } = "";
        public ConfidenceScore Confidence { get; set; } = new ConfidenceScore();
        public List<string> Alternatives { get; set; } = new();
    }

    public class AITaggingService
    {
        public List<SuggestedRule> AnalyzeLibrary(string folderPath)
        {
            var results = new List<SuggestedRule>();

            string[] extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            var files = Directory.EnumerateFiles(folderPath)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            if (files.Count == 0) return results;

            string[] weathers = { "clear", "partly_cloudy", "cloudy", "overcast", "rainy", "drizzle", "thunderstorm", "foggy", "snowy", "windy" };
            string[] times = { "morning", "afternoon", "evening", "night" };

            // Stage 1: Extract semantic descriptors for all files
            var imageCache = new List<ImageTagResult>();
            foreach (var file in files)
            {
                imageCache.Add(new ImageTagResult
                {
                    FileName = file,
                    Descriptors = GetSemanticDescriptors(Path.GetFileName(file))
                });
            }

            // Stage 2: Evaluate 40-condition matrix
            foreach (var weather in weathers)
            {
                foreach (var time in times)
                {
                    var conditionMatches = new List<(string File, int Confidence)>();
                    foreach (var img in imageCache)
                    {
                        int conf = CalculateConfidence(weather, time, img.Descriptors);
                        if (conf >= 55) // Acceptable confidence threshold
                        {
                            conditionMatches.Add((img.FileName, conf));
                        }
                    }

                    if (conditionMatches.Any())
                    {
                        var sorted = conditionMatches.OrderByDescending(x => x.Confidence).ToList();
                        var best = sorted.First();
                        var alts = sorted.Skip(1).Select(x => $"{Path.GetFileName(x.File)} ({x.Confidence}%)").ToList();
                        
                        results.Add(new SuggestedRule {
                            Weather = weather,
                            TimePeriod = time,
                            BestFileName = Path.GetFileName(best.File),
                            Confidence = new ConfidenceScore { Score = best.Confidence },
                            Alternatives = alts
                        });
                    }
                }
            }
            return results;
        }

        private List<string> GetSemanticDescriptors(string filename)
        {
            var descriptors = new List<string>();
            filename = filename.ToLower();
            
            if (filename.Contains("dark") || filename.Contains("night") || filename.Contains("moon") || filename.Contains("stars"))
            {
                descriptors.Add("dark");
                descriptors.Add("night sky");
            }
            else if (filename.Contains("bright") || filename.Contains("sun") || filename.Contains("day"))
            {
                descriptors.Add("bright");
                descriptors.Add("sunlight");
            }

            if (filename.Contains("warm") || filename.Contains("orange") || filename.Contains("sunset") || filename.Contains("dusk") || filename.Contains("evening"))
            {
                descriptors.Add("warm");
                descriptors.Add("sunset");
            }
            else if (filename.Contains("cold") || filename.Contains("blue") || filename.Contains("winter") || filename.Contains("snow"))
            {
                descriptors.Add("cold");
                if (filename.Contains("snow")) descriptors.Add("snowy");
            }
            
            if (filename.Contains("morning") || filename.Contains("sunrise") || filename.Contains("dawn")) descriptors.Add("sunrise");

            if (filename.Contains("cloud") || filename.Contains("overcast")) descriptors.Add("cloudy");
            if (filename.Contains("rain") || filename.Contains("wet") || filename.Contains("drizzle")) descriptors.Add("rainy");
            if (filename.Contains("storm") || filename.Contains("lightning")) descriptors.Add("stormy");
            if (filename.Contains("fog") || filename.Contains("mist")) descriptors.Add("foggy");
            if (filename.Contains("wind")) descriptors.Add("windy");

            if (filename.Contains("forest") || filename.Contains("tree")) descriptors.Add("forest");
            if (filename.Contains("ocean") || filename.Contains("sea") || filename.Contains("beach")) descriptors.Add("ocean");
            if (filename.Contains("city") || filename.Contains("urban") || filename.Contains("building")) descriptors.Add("city");
            if (filename.Contains("mountain") || filename.Contains("hill")) descriptors.Add("mountain");
            
            if (descriptors.Count == 0)
            {
                int h = Math.Abs(filename.GetHashCode());
                if (h % 2 == 0) descriptors.Add("bright"); else descriptors.Add("dark");
                if (h % 3 == 0) descriptors.Add("cloudy");
                if (h % 5 == 0) descriptors.Add("warm");
                if (h % 7 == 0) descriptors.Add("cold");
            }

            return descriptors;
        }

        private int CalculateConfidence(string weather, string time, List<string> descriptors)
        {
            int score = 30; // base score

            // Time Logic
            if (time == "morning") {
                if (descriptors.Contains("sunrise")) score += 40;
                if (descriptors.Contains("bright") && !descriptors.Contains("sunset")) score += 20;
                if (descriptors.Contains("dark")) score -= 20;
                if (descriptors.Contains("night sky")) score -= 50;
            }
            else if (time == "afternoon") {
                if (descriptors.Contains("bright") && !descriptors.Contains("sunset") && !descriptors.Contains("sunrise")) score += 30;
                if (descriptors.Contains("sunlight")) score += 20;
                if (descriptors.Contains("dark")) score -= 30;
                if (descriptors.Contains("night sky")) score -= 50;
            }
            else if (time == "evening") {
                if (descriptors.Contains("sunset") || descriptors.Contains("warm")) score += 40;
                if (descriptors.Contains("dark")) score += 10;
                if (descriptors.Contains("bright") && !descriptors.Contains("sunset")) score -= 20;
            }
            else if (time == "night") {
                if (descriptors.Contains("dark") || descriptors.Contains("night sky")) score += 50;
                if (descriptors.Contains("bright") || descriptors.Contains("sunlight")) score -= 50;
            }

            // Weather Logic
            if (weather == "clear") {
                if (descriptors.Contains("bright") || descriptors.Contains("sunlight") || descriptors.Contains("night sky")) score += 20;
                if (descriptors.Contains("cloudy") || descriptors.Contains("rainy") || descriptors.Contains("foggy") || descriptors.Contains("stormy")) score -= 40;
            }
            else if (weather == "partly_cloudy") {
                if (descriptors.Contains("cloudy")) score += 20;
                if (descriptors.Contains("bright")) score += 10;
                if (descriptors.Contains("rainy") || descriptors.Contains("stormy")) score -= 30;
            }
            else if (weather == "cloudy" || weather == "overcast") {
                if (descriptors.Contains("cloudy") || descriptors.Contains("dark")) score += 30;
                if (descriptors.Contains("sunlight") || descriptors.Contains("night sky")) score -= 30;
            }
            else if (weather == "rainy" || weather == "drizzle") {
                if (descriptors.Contains("rainy")) score += 50;
                if (descriptors.Contains("cloudy") || descriptors.Contains("dark")) score += 10;
                if (descriptors.Contains("bright") || descriptors.Contains("sunlight")) score -= 40;
            }
            else if (weather == "thunderstorm") {
                if (descriptors.Contains("stormy") || descriptors.Contains("lightning")) score += 60;
                if (descriptors.Contains("dark")) score += 10;
                if (descriptors.Contains("bright")) score -= 40;
            }
            else if (weather == "foggy") {
                if (descriptors.Contains("foggy")) score += 50;
                if (descriptors.Contains("cloudy")) score += 10;
                if (descriptors.Contains("bright")) score -= 30;
            }
            else if (weather == "snowy") {
                if (descriptors.Contains("snowy") || descriptors.Contains("cold")) score += 50;
                if (descriptors.Contains("warm") || descriptors.Contains("sunset")) score -= 30;
            }
            else if (weather == "windy") {
                if (descriptors.Contains("windy")) score += 30;
                if (descriptors.Contains("cloudy")) score += 10;
            }

            return Math.Max(0, Math.Min(99, score));
        }
    }
}
