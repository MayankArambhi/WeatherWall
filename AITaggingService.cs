using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

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

    public class WeatherEmbeddingsJson
    {
        public Dictionary<string, ConditionEmbeddingData> Conditions { get; set; } = new();
    }

    public class ConditionEmbeddingData
    {
        public List<float> Embedding { get; set; } = new();
        public string Prompt { get; set; } = "";
    }

    public class CachedAnalysisRoot
    {
        public string Model { get; set; } = "";
        public int Version { get; set; } = 2;
        public string Algorithm { get; set; } = "";
        public int TotalImages { get; set; } = 0;
        public int SuccessfullyEncoded { get; set; } = 0;
        public int FailedImages { get; set; } = 0;
        public int TotalConditions { get; set; } = 0;
        public Dictionary<string, CachedConditionAnalysis> Analysis { get; set; } = new();
    }

    public class CachedConditionAnalysis
    {
        [JsonPropertyName("best_image")]
        public string BestImage { get; set; } = "";
        
        [JsonPropertyName("best_confidence")]
        public float BestConfidence { get; set; } = 0f;
        
        [JsonPropertyName("top_3")]
        public List<List<object>> Top3 { get; set; } = new();
        
        [JsonPropertyName("all_scores")]
        public Dictionary<string, float> AllScores { get; set; } = new();
    }

    public class AITaggingService
    {
        private Dictionary<string, Dictionary<string, float>> _wallpaperScores = new();
        private AnalysisDiagnostics _diagnostics = new();

        /// <summary>
        /// Main analysis pipeline:
        /// 1. Check/Validate cache (wallpaper_analysis.json)
        /// 2. If invalid, download CLIP ONNX model if missing
        /// 3. Extract CLIP image embeddings locally using ONNX Runtime
        /// 4. Compare against text embeddings (40 conditions)
        /// 5. Perform global optimization and assign rules
        /// </summary>
        public List<OptimizedRule> AnalyzeLibrary(string folderPath, Action<string>? progressCallback = null)
        {
            _diagnostics = new();
            _wallpaperScores.Clear();

            // Step 1: Validate wallpaper folder
            if (!Directory.Exists(folderPath))
            {
                progressCallback?.Invoke($"ERROR: Wallpaper folder not found: {folderPath}");
                return new List<OptimizedRule>();
            }

            string[] extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
            var files = Directory.EnumerateFiles(folderPath)
                .Where(f => extensions.Contains(Path.GetExtension(f).ToLower()))
                .ToList();

            _diagnostics.TotalWallpapers = files.Count;

            if (files.Count == 0)
            {
                progressCallback?.Invoke("No wallpaper images found in folder");
                return new List<OptimizedRule>();
            }

            // Step 2: Check for existing analysis to use as cache
            string analysisFile = GetAnalysisFilePath(folderPath);
            bool isCacheValid = false;

            if (File.Exists(analysisFile))
            {
                progressCallback?.Invoke("Checking cached wallpaper analysis...");
                isCacheValid = ValidateCache(analysisFile, files);
                if (isCacheValid)
                {
                    progressCallback?.Invoke("Loading analysis from cache...");
                    if (ParseWallpaperAnalysis(analysisFile, files))
                    {
                        var cachedRules = PerformGlobalOptimization();
                        progressCallback?.Invoke("Analysis loaded successfully from cache!");
                        return cachedRules;
                    }
                }
                else
                {
                    progressCallback?.Invoke("Cache is missing some images or invalid. Running fresh analysis...");
                }
            }

            // Step 3: Run local ONNX analysis
            try
            {
                var runTask = Task.Run(() => RunLocalAnalysisAsync(folderPath, files, progressCallback));
                runTask.Wait();
                var optimizedRules = runTask.Result;
                
                // Log diagnostics
                LogDiagnostics();
                return optimizedRules;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                progressCallback?.Invoke($"Local analysis failed: {inner.Message}. Falling back to default rules.");
                Log($"ERROR in local analysis: {inner}");
                return GenerateFallbackRules(files);
            }
        }

        private string GetAnalysisFilePath(string folderPath)
        {
            var exeDir = AppContext.BaseDirectory;
            var candidate1 = Path.Combine(exeDir, "wallpaper_analysis.json");
            if (File.Exists(candidate1)) return candidate1;

            var projectRoot = Directory.GetParent(exeDir)?.Parent?.Parent?.Parent?.FullName;
            if (projectRoot != null)
            {
                var candidate2 = Path.Combine(projectRoot, "wallpaper_analysis.json");
                if (File.Exists(candidate2)) return candidate2;
            }

            return Path.Combine(folderPath, "wallpaper_analysis.json");
        }

        private string GetAnalysisWritePath(string folderPath)
        {
            var exeDir = AppContext.BaseDirectory;
            var candidate1 = Path.Combine(exeDir, "wallpaper_analysis.json");
            
            try
            {
                string testFile = Path.Combine(exeDir, ".write_test");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return candidate1;
            }
            catch
            {
                return Path.Combine(folderPath, "wallpaper_analysis.json");
            }
        }

        private bool ValidateCache(string cachePath, List<string> currentFiles)
        {
            try
            {
                string json = File.ReadAllText(cachePath);
                using JsonDocument doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("analysis", out var analysisElement))
                {
                    return false;
                }

                var firstCondition = analysisElement.EnumerateObject().FirstOrDefault();
                if (firstCondition.Value.ValueKind == JsonValueKind.Undefined)
                {
                    return false;
                }

                if (firstCondition.Value.TryGetProperty("all_scores", out var allScoresElement))
                {
                    var cachedFiles = allScoresElement.EnumerateObject().Select(p => p.Name).ToHashSet();
                    
                    foreach (var file in currentFiles)
                    {
                        string fileName = Path.GetFileName(file);
                        if (!cachedFiles.Contains(fileName))
                        {
                            return false;
                        }
                    }
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private string GetModelPath()
        {
            var exeDir = AppContext.BaseDirectory;
            var path = Path.Combine(exeDir, "vision_model_quantized.onnx");
            
            try
            {
                string testFile = Path.Combine(exeDir, ".write_test");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                return path;
            }
            catch
            {
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeatherWall");
                Directory.CreateDirectory(appData);
                return Path.Combine(appData, "vision_model_quantized.onnx");
            }
        }

        private async Task DownloadModelAsync(string downloadUrl, string destinationPath, Action<string>? progressCallback)
        {
            progressCallback?.Invoke("Initializing download of local AI model (approx. 84 MB)...");
            
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = destinationPath + ".tmp";
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromMinutes(10);
                
                using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        var totalRead = 0L;
                        int read;
                        var lastProgressUpdate = DateTime.MinValue;

                        while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, read);
                            totalRead += read;
                            
                            if ((DateTime.Now - lastProgressUpdate).TotalMilliseconds > 100)
                            {
                                lastProgressUpdate = DateTime.Now;
                                if (totalBytes != -1)
                                {
                                    double percentage = (double)totalRead / totalBytes * 100;
                                    double totalMb = (double)totalBytes / (1024 * 1024);
                                    double currentMb = (double)totalRead / (1024 * 1024);
                                    progressCallback?.Invoke($"Downloading AI model: {percentage:F1}% ({currentMb:F1} MB / {totalMb:F1} MB)...");
                                }
                                else
                                {
                                    double currentMb = (double)totalRead / (1024 * 1024);
                                    progressCallback?.Invoke($"Downloading AI model: {currentMb:F1} MB downloaded...");
                                }
                            }
                        }
                    }
                }
            }

            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(tempPath, destinationPath);
            progressCallback?.Invoke("AI model downloaded successfully!");
        }

        private float[] PreprocessImage(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();

                int targetWidth = 224;
                int targetHeight = 224;
                
                double scaleX = (double)targetWidth / frame.PixelWidth;
                double scaleY = (double)targetHeight / frame.PixelHeight;

                var scale = new ScaleTransform(scaleX, scaleY);
                scale.Freeze();

                var resized = new TransformedBitmap(frame, scale);
                resized.Freeze();

                var converted = new FormatConvertedBitmap(resized, PixelFormats.Rgb24, null, 0);
                converted.Freeze();

                int stride = targetWidth * 3;
                byte[] pixels = new byte[targetHeight * stride];
                converted.CopyPixels(pixels, stride, 0);

                float[] normalizedData = new float[3 * 224 * 224];
                
                float[] mean = { 0.48145466f, 0.4578275f, 0.40821073f };
                float[] std = { 0.26862954f, 0.26130258f, 0.27577711f };

                for (int y = 0; y < 224; y++)
                {
                    for (int x = 0; x < 224; x++)
                    {
                        int pixelIndex = (y * 224 + x) * 3;
                        
                        float r = pixels[pixelIndex] / 255.0f;
                        float g = pixels[pixelIndex + 1] / 255.0f;
                        float b = pixels[pixelIndex + 2] / 255.0f;

                        normalizedData[0 * 224 * 224 + y * 224 + x] = (r - mean[0]) / std[0];
                        normalizedData[1 * 224 * 224 + y * 224 + x] = (g - mean[1]) / std[1];
                        normalizedData[2 * 224 * 224 + y * 224 + x] = (b - mean[2]) / std[2];
                    }
                }

                return normalizedData;
            }
        }

        private float[] L2Normalize(float[] vector)
        {
            float sumSq = 0f;
            for (int i = 0; i < vector.Length; i++)
            {
                sumSq += vector[i] * vector[i];
            }
            float norm = (float)Math.Sqrt(sumSq);
            if (norm > 0f)
            {
                for (int i = 0; i < vector.Length; i++)
                {
                    vector[i] /= norm;
                }
            }
            return vector;
        }

        private async Task<List<OptimizedRule>> RunLocalAnalysisAsync(string folderPath, List<string> files, Action<string>? progressCallback)
        {
            string modelPath = GetModelPath();
            
            if (!File.Exists(modelPath))
            {
                string downloadUrl = "https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model_quantized.onnx";
                await DownloadModelAsync(downloadUrl, modelPath, progressCallback);
            }
            
            progressCallback?.Invoke("Loading weather condition prompts...");
            string exeDir = AppContext.BaseDirectory;
            string embeddingsPath = Path.Combine(exeDir, "weather_embeddings.json");
            
            if (!File.Exists(embeddingsPath))
            {
                var projectRoot = Directory.GetParent(exeDir)?.Parent?.Parent?.Parent?.FullName;
                if (projectRoot != null)
                {
                    embeddingsPath = Path.Combine(projectRoot, "weather_embeddings.json");
                }
            }

            if (!File.Exists(embeddingsPath))
            {
                throw new FileNotFoundException("weather_embeddings.json not found in executable folder or project root");
            }

            string jsonText = await File.ReadAllTextAsync(embeddingsPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var embeddingsData = JsonSerializer.Deserialize<WeatherEmbeddingsJson>(jsonText, options);
            if (embeddingsData == null || embeddingsData.Conditions.Count == 0)
            {
                throw new Exception("Failed to deserialize weather_embeddings.json or no conditions found");
            }

            progressCallback?.Invoke("Loading local AI model into memory...");
            
            using var sessionOptions = new SessionOptions();
            sessionOptions.AppendExecutionProvider_CPU();
            
            using var session = new InferenceSession(modelPath, sessionOptions);
            
            var imageEmbeddings = new Dictionary<string, float[]>();
            var failedFiles = new List<string>();

            for (int i = 0; i < files.Count; i++)
            {
                string filePath = files[i];
                string fileName = Path.GetFileName(filePath);
                
                progressCallback?.Invoke($"AI processing image [{i + 1}/{files.Count}]: {fileName}...");

                try
                {
                    float[] normalizedPixels = PreprocessImage(filePath);

                    var inputTensor = new DenseTensor<float>(normalizedPixels, new int[] { 1, 3, 224, 224 });
                    
                    var inputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor("pixel_values", inputTensor)
                    };

                    using var results = session.Run(inputs);
                    
                    var outputValue = results.FirstOrDefault(r => r.Name == "image_embeds") ?? results.FirstOrDefault();
                    if (outputValue == null)
                    {
                        throw new Exception("No output tensor found in model");
                    }
                    
                    var rawEmbedding = outputValue.AsTensor<float>().ToArray();
                    var normalizedEmbedding = L2Normalize(rawEmbedding);
                    imageEmbeddings[fileName] = normalizedEmbedding;
                }
                catch (Exception ex)
                {
                    Log($"Failed to process image {fileName}: {ex.Message}");
                    failedFiles.Add(fileName);
                }
            }

            if (imageEmbeddings.Count == 0)
            {
                throw new Exception("No images could be processed by the local AI model");
            }

            progressCallback?.Invoke("Matching wallpapers to weather conditions...");
            
            var analysisResults = new Dictionary<string, CachedConditionAnalysis>();

            foreach (var condPair in embeddingsData.Conditions)
            {
                string condition = condPair.Key;
                float[] textEmb = condPair.Value.Embedding.ToArray();

                var allScores = new Dictionary<string, float>();
                
                foreach (var imgPair in imageEmbeddings)
                {
                    string fileName = imgPair.Key;
                    float[] imgEmb = imgPair.Value;

                    float similarity = 0f;
                    for (int j = 0; j < 512; j++)
                    {
                        similarity += imgEmb[j] * textEmb[j];
                    }

                    float confidence = Math.Max(0f, Math.Min(100f, (similarity + 1f) * 50f));
                    allScores[fileName] = confidence;
                }

                var sortedScores = allScores.OrderByDescending(s => s.Value).ToList();
                string bestImage = sortedScores[0].Key;
                float bestScore = sortedScores[0].Value;

                var top3 = sortedScores.Take(3).Select(s => new List<object> { s.Key, Math.Round(s.Value, 1) }).ToList();

                analysisResults[condition] = new CachedConditionAnalysis
                {
                    BestImage = bestImage,
                    BestConfidence = (float)Math.Round(bestScore, 1),
                    Top3 = top3,
                    AllScores = allScores.ToDictionary(k => k.Key, v => (float)Math.Round(v.Value, 1))
                };

                if (!_wallpaperScores.ContainsKey(condition))
                {
                    _wallpaperScores[condition] = new Dictionary<string, float>();
                }
                _wallpaperScores[condition][bestImage] = bestScore;
            }

            progressCallback?.Invoke("Saving analysis results...");
            
            var outputData = new CachedAnalysisRoot
            {
                Model = "clip-ViT-B-32-onnx-quantized",
                Version = 2,
                Algorithm = "condition-based-assignment",
                TotalImages = files.Count,
                SuccessfullyEncoded = imageEmbeddings.Count,
                FailedImages = failedFiles.Count,
                TotalConditions = embeddingsData.Conditions.Count,
                Analysis = analysisResults
            };

            string writePath = GetAnalysisWritePath(folderPath);
            var writeOptions = new JsonSerializerOptions { WriteIndented = true };
            string outputJson = JsonSerializer.Serialize(outputData, writeOptions);
            await File.WriteAllTextAsync(writePath, outputJson);

            _diagnostics.SuccessfullyAnalyzed = embeddingsData.Conditions.Count;
            _diagnostics.FailedFiles = failedFiles;
            _diagnostics.FailedAnalysis = failedFiles.Count;

            progressCallback?.Invoke("Optimizing rule assignments...");
            var optimizedRules = PerformGlobalOptimization();
            
            progressCallback?.Invoke("CLIP local analysis complete!");
            return optimizedRules;
        }

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

                foreach (var conditionElement in analysisElement.EnumerateObject())
                {
                    string condition = conditionElement.Name;
                    var conditionData = conditionElement.Value;

                    try
                    {
                        string bestImage = conditionData.GetProperty("best_image").GetString() ?? "";
                        float bestConfidence = conditionData.GetProperty("best_confidence").GetSingle();

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
                _diagnostics.FailedAnalysis = 0;

                Log($"Loaded CLIP analysis for {processedCount} conditions");
                return true;
            }
            catch (Exception e)
            {
                Log($"ERROR parsing wallpaper analysis: {e.Message}");
                return false;
            }
        }

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

            foreach (var condition in allConditions)
            {
                if (!_wallpaperScores.ContainsKey(condition) || _wallpaperScores[condition].Count == 0)
                {
                    Log($"  ✗ NO MATCH for {condition}");
                    continue;
                }

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
                    Alternatives = new List<string>()
                };

                result.Add(rule);

                string status = rule.NeedsReview ? "⚠ REVIEW" : "✓";
                Log($"  {status} {condition:35s} → {selectedImage:30s} ({confidence:5.1f}%)");
            }

            _diagnostics.ConditionsWithMatches = result.Count;
            _diagnostics.RulesNeedingReview = result.Count(r => r.NeedsReview);

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

        private string ParseCondition(string condition, out string weather, out string timePeriod)
        {
            var parts = condition.Split('_');
            if (parts.Length < 2)
            {
                weather = "clear";
                timePeriod = "morning";
                return condition;
            }

            timePeriod = parts[^1];
            weather = string.Join("_", parts.Take(parts.Length - 1));

            return condition;
        }

        private List<OptimizedRule> GenerateFallbackRules(List<string> files)
        {
            Log("\n" + new string('=', 70));
            Log("⚠⚠⚠ USING FALLBACK RULES ⚠⚠⚠");
            Log("wallpaper_analysis.json not found - using filename heuristics");
            Log("This will produce POOR results. RUN local AI analysis!");
            Log(new string('=', 70) + "\n");
            
            var results = new List<OptimizedRule>();
            
            string[] weathers = { "clear", "partly_cloudy", "cloudy", "overcast", "rainy", "drizzle", "thunderstorm", "foggy", "snowy", "windy" };
            string[] times = { "morning", "afternoon", "evening", "night" };

            foreach (var weather in weathers)
            {
                foreach (var time in times)
                {
                    var searchTerms = new[] { weather, time }.Where(t => !string.IsNullOrEmpty(t)).ToList();
                    var matches = files.Where(f => searchTerms.Any(t => f.ToLower().Contains(t))).ToList();

                    string selectedFile = matches.Count > 0 
                        ? Path.GetFileName(matches[0]) 
                        : Path.GetFileName(files[0]);
                    
                    var alternatives = matches.Count > 0
                        ? matches.Skip(1).Take(2).Select(f => Path.GetFileName(f)!).Where(f => !string.IsNullOrEmpty(f)).ToList()
                        : new List<string> { Path.GetFileName(files[0])! };

                    results.Add(new OptimizedRule
                    {
                        Weather = weather,
                        TimePeriod = time,
                        SelectedFileName = selectedFile,
                        Confidence = matches.Count > 0 ? 50f : 25f,
                        NeedsReview = true,
                        Alternatives = alternatives
                    });
                }
            }

            _diagnostics.RulesNeedingReview = results.Count;
            Log($"Created {results.Count} fallback rules (all marked for review)");
            return results;
        }

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

        private void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine($"[AITagging] {message}");
            Console.WriteLine($"[AITagging] {message}");
        }
    }
}
