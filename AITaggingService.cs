using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeatherWall
{
    /// <summary>
    /// Represents a single image-condition match with confidence score
    /// </summary>
    public class MatchScore
    {
        public string FileName { get; set; } = "";
        public string Condition { get; set; } = "";
        public float Confidence { get; set; } = 0f;
        public int Rank { get; set; } = 0;
    }

    /// <summary>
    /// Represents a condition's top 3 matching wallpapers
    /// </summary>
    public class ConditionMatches
    {
        public string Condition { get; set; } = "";
        public List<MatchScore> Matches { get; set; } = new();
        public string PrimaryWallpaper => Matches.FirstOrDefault()?.FileName ?? "";
        public float PrimaryConfidence => Matches.FirstOrDefault()?.Confidence ?? 0f;
        public bool NeedsReview => PrimaryConfidence < 65f;
    }

    /// <summary>
    /// Final optimized rule assignment
    /// </summary>
    public class OptimizedRule
    {
        public string Weather { get; set; } = "";
        public string TimePeriod { get; set; } = "";
        public string SelectedFileName { get; set; } = "";
        public float Confidence { get; set; } = 0f;
        public bool NeedsReview { get; set; } = false;
        public List<string> Alternatives { get; set; } = new();
    }

    /// <summary>
    /// Diagnostic information for analysis
    /// </summary>
    public class AnalysisDiagnostics
    {
        public int TotalWallpapers { get; set; } = 0;
        public int SuccessfullyAnalyzed { get; set; } = 0;
        public int FailedAnalysis { get; set; } = 0;
        public int ConditionsWithMatches { get; set; } = 0;
        public int RulesNeedingReview { get; set; } = 0;
        public List<string> FailedFiles { get; set; } = new();
        public Dictionary<string, List<string>> DuplicateWallpaperAssignments { get; set; } = new();
        public string AnalysisTimestamp { get; set; } = DateTime.UtcNow.ToString("o");
    }

    public class AITaggingService
    {
        private Dictionary<string, Dictionary<string, float>> _wallpaperScores = new();
        private AnalysisDiagnostics _diagnostics = new();

        /// <summary>
        /// Main analysis pipeline:
        /// 1. Extract CLIP image embeddings (via Python script)
        /// 2. Compare against text embeddings (40 conditions)
        /// 3. Perform global optimization
        /// 4. Assign unique wallpaper per condition
        /// </summary>
        public List<OptimizedRule> AnalyzeLibrary(string folderPath)
        {
            _diagnostics = new();
            _wallpaperScores.Clear();

            // Step 1: Validate wallpaper folder
            if (!Directory.Exists(folderPath))
            {
                Log($"ERROR: Wallpaper folder not found: {folderPath}");
                return new List<OptimizedRule>();
            }

            string[] extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            var files = Directory.EnumerateFiles(folderPath)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            _diagnostics.TotalWallpapers = files.Count;

            if (files.Count == 0)
            {
                Log("No wallpaper images found in folder");
                return new List<OptimizedRule>();
            }

            // Step 2: Load pre-computed analysis from Python script
            // Try multiple locations: exe dir, project root, wallpaper folder
            string analysisFile = null;
            
            // Try 1: Same directory as executable
            var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
            var candidate1 = Path.Combine(exeDir, "wallpaper_analysis.json");
            if (File.Exists(candidate1))
            {
                analysisFile = candidate1;
                Log($"Found analysis file in exe directory: {analysisFile}");
            }
            
            // Try 2: Project root (several levels up from exe)
            if (analysisFile == null)
            {
                var projectRoot = Directory.GetParent(exeDir)?.Parent?.Parent?.Parent?.FullName;
                if (projectRoot != null)
                {
                    var candidate2 = Path.Combine(projectRoot, "wallpaper_analysis.json");
                    if (File.Exists(candidate2))
                    {
                        analysisFile = candidate2;
                        Log($"Found analysis file in project root: {analysisFile}");
                    }
                }
            }
            
            // Try 3: Wallpaper folder
            if (analysisFile == null)
            {
                var candidate3 = Path.Combine(folderPath, "wallpaper_analysis.json");
                if (File.Exists(candidate3))
                {
                    analysisFile = candidate3;
                    Log($"Found analysis file in wallpaper folder: {analysisFile}");
                }
            }

            if (analysisFile == null || !File.Exists(analysisFile))
            {
                Log($"ERROR: wallpaper_analysis.json not found. Run analyze_wallpapers.py first");
                return GenerateFallbackRules(files);
            }

            // Step 3: Parse CLIP analysis results
            if (!ParseWallpaperAnalysis(analysisFile, files))
            {
                Log("Failed to parse wallpaper analysis");
                return GenerateFallbackRules(files);
            }

            // Step 4: Global optimization - assign unique wallpaper per condition
            var optimizedRules = PerformGlobalOptimization();

            // Step 5: Log diagnostics
            LogDiagnostics();

            return optimizedRules;
        }

        /// <summary>
        /// Load wallpaper analysis from JSON and extract condition->image assignments
        /// New format: analysis[condition] = { best_image, best_confidence, top_3, all_scores }
        /// </summary>
        private bool ParseWallpaperAnalysis(string analysisFilePath, List<string> availableFiles)
        {
            try
            {
                string json = File.ReadAllText(analysisFilePath);
                using JsonDocument doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("analysis", out var analysisElement))
                {
                    Log("ERROR: 'analysis' property not found in wallpaper_analysis.json");
                    return false;
                }

                int processedCount = 0;

                // New format: iterate through conditions
                foreach (var conditionElement in analysisElement.EnumerateObject())
                {
                    string condition = conditionElement.Name;
                    var conditionData = conditionElement.Value;

                    try
                    {
                        string bestImage = conditionData.GetProperty("best_image").GetString() ?? "";
                        float bestConfidence = conditionData.GetProperty("best_confidence").GetSingle();

                        // Store in condition->image+confidence format
                        if (!_wallpaperScores.ContainsKey(condition))
                        {
                            _wallpaperScores[condition] = new Dictionary<string, float>();
                        }
                        _wallpaperScores[condition][bestImage] = bestConfidence;

                        processedCount++;
                    }
                    catch (Exception e)
                    {
                        Log($"Warning: Failed to parse condition {condition}: {e.Message}");
                    }
                }

                _diagnostics.SuccessfullyAnalyzed = processedCount;
                _diagnostics.FailedAnalysis = 0; // New format doesn't have per-image failures tracked

                Log($"Loaded CLIP analysis for {processedCount} conditions");
                return true;
            }
            catch (Exception e)
            {
                Log($"ERROR parsing wallpaper analysis: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Global optimization algorithm (simplified for condition-based analysis):
        /// Each condition already has its best image pre-computed by analyze_wallpapers.py
        /// Just iterate through conditions and create rules
        /// </summary>
        private List<OptimizedRule> PerformGlobalOptimization()
        {
            var result = new List<OptimizedRule>();
            var allConditions = new[] {
                "clear_morning", "clear_afternoon", "clear_evening", "clear_night",
                "partly_cloudy_morning", "partly_cloudy_afternoon", "partly_cloudy_evening", "partly_cloudy_night",
                "cloudy_morning", "cloudy_afternoon", "cloudy_evening", "cloudy_night",
                "overcast_morning", "overcast_afternoon", "overcast_evening", "overcast_night",
                "rainy_morning", "rainy_afternoon", "rainy_evening", "rainy_night",
                "drizzle_morning", "drizzle_afternoon", "drizzle_evening", "drizzle_night",
                "thunderstorm_morning", "thunderstorm_afternoon", "thunderstorm_evening", "thunderstorm_night",
                "foggy_morning", "foggy_afternoon", "foggy_evening", "foggy_night",
                "snowy_morning", "snowy_afternoon", "snowy_evening", "snowy_night",
                "windy_morning", "windy_afternoon", "windy_evening", "windy_night"
            };

            // With condition-based analysis, each condition has its best_image pre-computed
            foreach (var condition in allConditions)
            {
                if (!_wallpaperScores.ContainsKey(condition) || _wallpaperScores[condition].Count == 0)
                {
                    Log($"  ✗ NO MATCH for {condition}");
                    continue;
                }

                // Get best image for this condition (pre-computed by analyze_wallpapers.py)
                var bestMatch = _wallpaperScores[condition].FirstOrDefault();
                string selectedImage = bestMatch.Key;
                float confidence = bestMatch.Value;

                ParseCondition(condition, out string weather, out string timePeriod);

                var rule = new OptimizedRule
                {
                    Weather = weather,
                    TimePeriod = timePeriod,
                    SelectedFileName = selectedImage,
                    Confidence = confidence,
                    NeedsReview = confidence < 65f,
                    Alternatives = new List<string>()  // TODO: Could pull from top_3 if needed
                };

                result.Add(rule);

                string status = rule.NeedsReview ? "⚠ REVIEW" : "✓";
                Log($"  {status} {condition:35s} → {selectedImage:30s} ({confidence:5.1f}%)");
            }

            _diagnostics.ConditionsWithMatches = result.Count;
            _diagnostics.RulesNeedingReview = result.Count(r => r.NeedsReview);

            // Track image reuse statistics
            var imageUsage = result.GroupBy(r => r.SelectedFileName);
            int reusedCount = imageUsage.Count(g => g.Count() > 1);
            int totalReuses = imageUsage.Sum(g => g.Count() - 1);
            
            if (reusedCount > 0)
            {
                Log($"\n  ℹ Image Reuse: {reusedCount} images assigned to multiple conditions ({totalReuses} total reuses)");
                foreach (var group in imageUsage.Where(g => g.Count() > 1).Take(5))
                {
                    var assignments = string.Join(", ", group.Take(3).Select(r => $"{r.Weather}/{r.TimePeriod}"));
                    Log($"    • {group.Key} → {assignments}" + (group.Count() > 3 ? $" +{group.Count() - 3} more" : ""));
                }
            }

            return result;
        }

        /// <summary>
        /// Parse condition string (e.g., "clear_morning") into weather and time period
        /// </summary>
        private string ParseCondition(string condition, out string weather, out string timePeriod)
        {
            var parts = condition.Split('_');
            if (parts.Length < 2)
            {
                weather = "clear";
                timePeriod = "morning";
                return condition;
            }

            timePeriod = parts[^1]; // Last element
            weather = string.Join("_", parts.Take(parts.Length - 1)); // Everything except last

            return condition;
        }

        /// <summary>
        /// Fallback rules if analysis fails - distribute by filename patterns
        /// </summary>
        private List<OptimizedRule> GenerateFallbackRules(List<string> files)
        {
            Log("\n" + new string('=', 70));
            Log("⚠⚠⚠ USING FALLBACK RULES ⚠⚠⚠");
            Log("wallpaper_analysis.json not found - using filename heuristics");
            Log("This will produce POOR results. RUN analyze_wallpapers.py!");
            Log(new string('=', 70) + "\n");
            
            var results = new List<OptimizedRule>();
            
            string[] weathers = { "clear", "partly_cloudy", "cloudy", "overcast", "rainy", "drizzle", "thunderstorm", "foggy", "snowy", "windy" };
            string[] times = { "morning", "afternoon", "evening", "night" };

            foreach (var weather in weathers)
            {
                foreach (var time in times)
                {
                    // Find wallpaper matching weather+time combination
                    var searchTerms = new[] { weather, time }.Where(t => !string.IsNullOrEmpty(t)).ToList();
                    var matches = files.Where(f => searchTerms.Any(t => f.ToLower().Contains(t))).ToList();

                    // If no match, use first available file (fallback)
                    string selectedFile = matches.Count > 0 
                        ? Path.GetFileName(matches[0]) 
                        : Path.GetFileName(files[0]); // Fallback to first file
                    
                    var alternatives = matches.Count > 0
                        ? matches.Skip(1).Take(2).Select(Path.GetFileName).Where(f => !string.IsNullOrEmpty(f)).ToList() ?? new()
                        : new List<string> { Path.GetFileName(files[0]) }; // At least show the fallback option

                    results.Add(new OptimizedRule
                    {
                        Weather = weather,
                        TimePeriod = time,
                        SelectedFileName = selectedFile,
                        Confidence = matches.Count > 0 ? 50f : 25f,  // Lower confidence if fallback used
                        NeedsReview = true,  // All fallback rules need review
                        Alternatives = alternatives
                    });
                }
            }

            _diagnostics.RulesNeedingReview = results.Count;
            Log($"Created {results.Count} fallback rules (all marked for review)");
            return results;
        }

        /// <summary>
        /// Log diagnostics to console
        /// </summary>
        private void LogDiagnostics()
        {
            Log("\n" + new string('=', 70));
            Log("ANALYSIS DIAGNOSTICS");
            Log(new string('=', 70));
            Log($"Total wallpapers:          {_diagnostics.TotalWallpapers}");
            Log($"Successfully analyzed:     {_diagnostics.SuccessfullyAnalyzed}");
            Log($"Failed analysis:           {_diagnostics.FailedAnalysis}");
            Log($"Conditions with matches:   {_diagnostics.ConditionsWithMatches}");
            Log($"Rules needing review:      {_diagnostics.RulesNeedingReview}");

            if (_diagnostics.DuplicateWallpaperAssignments.Any())
            {
                Log("\nDuplicate assignments detected:");
                foreach (var dup in _diagnostics.DuplicateWallpaperAssignments)
                {
                    Log($"  {dup.Key} → {string.Join(", ", dup.Value)}");
                }
            }

            Log(new string('=', 70) + "\n");
        }

        /// <summary>
        /// Log message (would integrate with app's logging system)
        /// </summary>
        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[AITagging] {message}");
            Console.WriteLine($"[AITagging] {message}");
        }
    }
}
