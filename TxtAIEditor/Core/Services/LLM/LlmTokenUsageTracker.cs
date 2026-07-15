using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TxtAIEditor.Core.Services;

namespace TxtAIEditor.Core.Services.LLM
{
    internal sealed class LlmTokenUsageTracker
    {
        private readonly object _tokenUsageStatsLock = new();
        private readonly Dictionary<string, MutableTokenUsageBucket> _tokenUsageBuckets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MutableTokenUsagePeriodBucket> _tokenUsageDayBuckets = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MutableTokenUsagePeriodBucket> _tokenUsageMonthBuckets = new(StringComparer.OrdinalIgnoreCase);
        private LlmTokenUsageStats _tokenUsageStats = new();
        private LlmTokenUsage? _lastTokenUsage;

        public LlmTokenUsageTracker()
        {
            LoadTokenUsageStats();
        }


        public LlmTokenUsage? LastTokenUsage
        {
            get
            {
                lock (_tokenUsageStatsLock)
                {
                    return _lastTokenUsage;
                }
            }
        }

        public LlmTokenUsageStats TokenUsageStats
        {
            get
            {
                lock (_tokenUsageStatsLock)
                {
                    return _tokenUsageStats;
                }
            }
        }

        public void ResetTokenUsageStats()
        {
            lock (_tokenUsageStatsLock)
            {
                _lastTokenUsage = null;
                _tokenUsageBuckets.Clear();
                _tokenUsageDayBuckets.Clear();
                _tokenUsageMonthBuckets.Clear();
                _tokenUsageStats = new LlmTokenUsageStats();
            }

            DeleteTokenUsageStatsFile();
        }

        public void Record(LlmTokenUsage usage)
        {
            if (!usage.HasAny)
            {
                return;
            }

            LlmTokenUsageStats snapshot;
            lock (_tokenUsageStatsLock)
            {
                _lastTokenUsage = usage;
                string provider = string.IsNullOrWhiteSpace(usage.Provider) ? "Unknown" : usage.Provider.Trim();
                string model = string.IsNullOrWhiteSpace(usage.Model) ? "Unknown" : usage.Model.Trim();
                string bucketKey = $"{provider}\u001f{model}";

                if (!_tokenUsageBuckets.TryGetValue(bucketKey, out var bucket))
                {
                    bucket = new MutableTokenUsageBucket
                    {
                        Provider = provider,
                        Model = model
                    };
                    _tokenUsageBuckets[bucketKey] = bucket;
                }

                bucket.RequestCount++;
                bucket.PromptTokens += usage.PromptTokens ?? 0;
                bucket.CompletionTokens += usage.CompletionTokens ?? 0;
                bucket.TotalTokens += usage.TotalTokens ?? 0;
                bucket.CachedTokens += usage.CachedTokens ?? 0;

                DateTimeOffset observedAt = usage.ObservedAt ?? DateTimeOffset.Now;
                AddPeriodUsage(_tokenUsageDayBuckets, observedAt.ToLocalTime().ToString("yyyy-MM-dd"), usage);
                AddPeriodUsage(_tokenUsageMonthBuckets, observedAt.ToLocalTime().ToString("yyyy-MM"), usage);

                _tokenUsageStats = CreateTokenUsageStatsSnapshot();
                snapshot = _tokenUsageStats;
            }

            SaveTokenUsageStats(snapshot);
        }

        private LlmTokenUsageStats CreateTokenUsageStatsSnapshot()
        {
            var buckets = _tokenUsageBuckets.Values
                .OrderByDescending(item => item.CachedTokens)
                .ThenByDescending(item => item.TotalTokens)
                .Select(item => item.ToSnapshot())
                .ToArray();
            var dayBuckets = _tokenUsageDayBuckets.Values
                .OrderBy(item => item.Period)
                .Select(item => item.ToSnapshot())
                .ToArray();
            var monthBuckets = _tokenUsageMonthBuckets.Values
                .OrderBy(item => item.Period)
                .Select(item => item.ToSnapshot())
                .ToArray();

            return new LlmTokenUsageStats
            {
                RequestCount = buckets.Sum(item => item.RequestCount),
                PromptTokens = buckets.Sum(item => item.PromptTokens),
                CompletionTokens = buckets.Sum(item => item.CompletionTokens),
                TotalTokens = buckets.Sum(item => item.TotalTokens),
                CachedTokens = buckets.Sum(item => item.CachedTokens),
                LastUsage = _lastTokenUsage,
                ByProviderModel = buckets,
                ByDay = dayBuckets,
                ByMonth = monthBuckets
            };
        }

        private static void AddPeriodUsage(Dictionary<string, MutableTokenUsagePeriodBucket> buckets, string period, LlmTokenUsage usage)
        {
            if (!buckets.TryGetValue(period, out var bucket))
            {
                bucket = new MutableTokenUsagePeriodBucket
                {
                    Period = period
                };
                buckets[period] = bucket;
            }

            bucket.RequestCount++;
            bucket.PromptTokens += usage.PromptTokens ?? 0;
            bucket.CompletionTokens += usage.CompletionTokens ?? 0;
            bucket.TotalTokens += usage.TotalTokens ?? 0;
            bucket.CachedTokens += usage.CachedTokens ?? 0;
        }

        private void LoadTokenUsageStats()
        {
            try
            {
                string path = GetTokenUsageStatsPath();
                if (!File.Exists(path))
                {
                    return;
                }

                string json = File.ReadAllText(path);
                var persisted = JsonSerializer.Deserialize<PersistedTokenUsageStats>(json);
                if (persisted == null)
                {
                    return;
                }

                lock (_tokenUsageStatsLock)
                {
                    _tokenUsageBuckets.Clear();
                    _tokenUsageDayBuckets.Clear();
                    _tokenUsageMonthBuckets.Clear();

                    foreach (var bucket in persisted.ByProviderModel ?? new List<LlmTokenUsageBucket>())
                    {
                        string key = $"{bucket.Provider}\u001f{bucket.Model}";
                        _tokenUsageBuckets[key] = new MutableTokenUsageBucket
                        {
                            Provider = bucket.Provider,
                            Model = bucket.Model,
                            RequestCount = bucket.RequestCount,
                            PromptTokens = bucket.PromptTokens,
                            CompletionTokens = bucket.CompletionTokens,
                            TotalTokens = bucket.TotalTokens,
                            CachedTokens = bucket.CachedTokens
                        };
                    }

                    foreach (var bucket in persisted.ByDay ?? new List<LlmTokenUsagePeriodBucket>())
                    {
                        _tokenUsageDayBuckets[bucket.Period] = new MutableTokenUsagePeriodBucket
                        {
                            Period = bucket.Period,
                            RequestCount = bucket.RequestCount,
                            PromptTokens = bucket.PromptTokens,
                            CompletionTokens = bucket.CompletionTokens,
                            TotalTokens = bucket.TotalTokens,
                            CachedTokens = bucket.CachedTokens
                        };
                    }

                    foreach (var bucket in persisted.ByMonth ?? new List<LlmTokenUsagePeriodBucket>())
                    {
                        _tokenUsageMonthBuckets[bucket.Period] = new MutableTokenUsagePeriodBucket
                        {
                            Period = bucket.Period,
                            RequestCount = bucket.RequestCount,
                            PromptTokens = bucket.PromptTokens,
                            CompletionTokens = bucket.CompletionTokens,
                            TotalTokens = bucket.TotalTokens,
                            CachedTokens = bucket.CachedTokens
                        };
                    }

                    _lastTokenUsage = persisted.LastUsage;
                    _tokenUsageStats = CreateTokenUsageStatsSnapshot();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed loading LLM token usage stats: {ex.Message}");
            }
        }

        private void SaveTokenUsageStats(LlmTokenUsageStats stats)
        {
            try
            {
                string path = GetTokenUsageStatsPath();
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var persisted = new PersistedTokenUsageStats
                {
                    LastUsage = stats.LastUsage,
                    ByProviderModel = stats.ByProviderModel.ToList(),
                    ByDay = stats.ByDay.ToList(),
                    ByMonth = stats.ByMonth.ToList()
                };
                string json = JsonSerializer.Serialize(persisted, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed saving LLM token usage stats: {ex.Message}");
            }
        }

        private void DeleteTokenUsageStatsFile()
        {
            try
            {
                string path = GetTokenUsageStatsPath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed deleting LLM token usage stats: {ex.Message}");
            }
        }

        private static string GetTokenUsageStatsPath()
        {
            return Path.Combine(SettingsBackupService.SettingsDirectoryPath, "llm-token-usage-stats.json");
        }

        private sealed class MutableTokenUsageBucket
        {
            public string Provider { get; init; } = string.Empty;
            public string Model { get; init; } = string.Empty;
            public int RequestCount { get; set; }
            public long PromptTokens { get; set; }
            public long CompletionTokens { get; set; }
            public long TotalTokens { get; set; }
            public long CachedTokens { get; set; }

            public LlmTokenUsageBucket ToSnapshot()
            {
                return new LlmTokenUsageBucket
                {
                    Provider = Provider,
                    Model = Model,
                    RequestCount = RequestCount,
                    PromptTokens = PromptTokens,
                    CompletionTokens = CompletionTokens,
                    TotalTokens = TotalTokens,
                    CachedTokens = CachedTokens
                };
            }
        }

        private sealed class MutableTokenUsagePeriodBucket
        {
            public string Period { get; init; } = string.Empty;
            public int RequestCount { get; set; }
            public long PromptTokens { get; set; }
            public long CompletionTokens { get; set; }
            public long TotalTokens { get; set; }
            public long CachedTokens { get; set; }

            public LlmTokenUsagePeriodBucket ToSnapshot()
            {
                return new LlmTokenUsagePeriodBucket
                {
                    Period = Period,
                    RequestCount = RequestCount,
                    PromptTokens = PromptTokens,
                    CompletionTokens = CompletionTokens,
                    TotalTokens = TotalTokens,
                    CachedTokens = CachedTokens
                };
            }
        }

        private sealed class PersistedTokenUsageStats
        {
            public LlmTokenUsage? LastUsage { get; set; }
            public List<LlmTokenUsageBucket> ByProviderModel { get; set; } = new();
            public List<LlmTokenUsagePeriodBucket> ByDay { get; set; } = new();
            public List<LlmTokenUsagePeriodBucket> ByMonth { get; set; } = new();
        }
    }
}
