using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;

namespace MediaInfoKeeper.Services {
    /// <summary>
    /// Bangumi 解析结果的耐久存储。刻意放在 data 目录而非 Emby 的 cache 目录，
    /// 因为 Emby 可能清理 cache，而这里保存的是「nfo 被外部工具覆盖后重建所需」的映射关系。
    /// </summary>
    internal static class BangumiStore {
        private static readonly TimeSpan SaveThrottle = TimeSpan.FromSeconds(2);
        private static readonly object SyncRoot = new();
        private static readonly JsonSerializerOptions JsonOptions = new() {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private static BangumiStoreDocument document;
        private static bool dirty;
        private static DateTime lastSaveUtc = DateTime.MinValue;

        public static bool TryGetSubjectId(string tmdbKey, out long subjectId) {
            subjectId = 0;
            if (string.IsNullOrWhiteSpace(tmdbKey)) return false;

            lock (SyncRoot) {
                EnsureLoaded();
                return document.Subjects.TryGetValue(tmdbKey, out subjectId) && subjectId > 0;
            }
        }

        public static void SetSubjectId(string tmdbKey, long subjectId) {
            if (string.IsNullOrWhiteSpace(tmdbKey) || subjectId <= 0) return;

            lock (SyncRoot) {
                EnsureLoaded();
                if (document.Subjects.TryGetValue(tmdbKey, out var existing) && existing == subjectId) return;

                document.Subjects[tmdbKey] = subjectId;
                dirty = true;
                SaveIfDue();
            }
        }

        public static string GetRoleName(long subjectId, string personKey) {
            if (subjectId <= 0 || string.IsNullOrWhiteSpace(personKey)) return null;

            lock (SyncRoot) {
                EnsureLoaded();
                return document.Roles.TryGetValue(ComposeRoleKey(subjectId, personKey), out var role) ? role : null;
            }
        }

        public static void SetRoleName(long subjectId, string personKey, string roleName) {
            if (subjectId <= 0 || string.IsNullOrWhiteSpace(personKey) || string.IsNullOrWhiteSpace(roleName)) return;

            lock (SyncRoot) {
                EnsureLoaded();
                var key = ComposeRoleKey(subjectId, personKey);
                if (document.Roles.TryGetValue(key, out var existing) &&
                    string.Equals(existing, roleName, StringComparison.Ordinal))
                    return;

                document.Roles[key] = roleName;
                dirty = true;
                SaveIfDue();
            }
        }

        public static BangumiStoredRating GetRating(long subjectId) {
            if (subjectId <= 0) return null;

            lock (SyncRoot) {
                EnsureLoaded();
                return document.Ratings.TryGetValue(subjectId.ToString(), out var rating) ? rating : null;
            }
        }

        public static void SetRating(long subjectId, BangumiStoredRating rating) {
            if (subjectId <= 0 || rating == null || rating.Score <= 0) return;

            lock (SyncRoot) {
                EnsureLoaded();
                var key = subjectId.ToString();
                if (document.Ratings.TryGetValue(key, out var existing) &&
                    existing != null &&
                    Math.Abs(existing.Score - rating.Score) < 0.001 &&
                    existing.Total == rating.Total)
                    return;

                document.Ratings[key] = rating;
                dirty = true;
                SaveIfDue();
            }
        }

        public static void Flush() {
            lock (SyncRoot) {
                if (!dirty) return;

                Save();
            }
        }

        private static string ComposeRoleKey(long subjectId, string personKey) {
            return subjectId + ":" + personKey;
        }

        private static void EnsureLoaded() {
            if (document != null) return;

            document = new BangumiStoreDocument();

            try {
                var path = GetStorePath();
                if (!File.Exists(path)) return;

                var body = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<BangumiStoreDocument>(body, JsonOptions);
                if (loaded != null) document = loaded;
            }
            catch (Exception ex) {
                Plugin.SharedLogger?.Debug("BangumiStore 读取失败: msg={0}", ex.Message);
            }

            document.Subjects ??= new Dictionary<string, long>(StringComparer.Ordinal);
            document.Roles ??= new Dictionary<string, string>(StringComparer.Ordinal);
            document.Ratings ??= new Dictionary<string, BangumiStoredRating>(StringComparer.Ordinal);
        }

        private static void SaveIfDue() {
            var now = DateTime.UtcNow;
            if (now - lastSaveUtc < SaveThrottle) return;

            Save();
        }

        private static void Save() {
            try {
                var path = GetStorePath();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));
                dirty = false;
                lastSaveUtc = DateTime.UtcNow;
            }
            catch (Exception ex) {
                Plugin.SharedLogger?.Debug("BangumiStore 写入失败: msg={0}", ex.Message);
            }
        }

        private static string GetStorePath() {
            return Path.Combine(GetDataRootFolder(), "bangumi", "mappings.json");
        }

        internal static string GetDataRootFolder() {
            try {
                var programDataPath = Plugin.Instance?.AppHost?.Resolve<IApplicationPaths>()?.ProgramDataPath;
                if (!string.IsNullOrWhiteSpace(programDataPath))
                    return Path.Combine(programDataPath, "data", Plugin.PluginName);
            }
            catch {
            }

            return Path.Combine("/config", "data", Plugin.PluginName);
        }
    }

    internal sealed class BangumiStoreDocument {
        public Dictionary<string, long> Subjects { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Roles { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, BangumiStoredRating> Ratings { get; set; } = new(StringComparer.Ordinal);
    }

    internal sealed class BangumiStoredRating {
        public double Score { get; set; }

        public int Total { get; set; }
    }
}
