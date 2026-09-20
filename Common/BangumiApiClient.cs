using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;

namespace MediaInfoKeeper.Common {
    internal static class BangumiApiClient {
        public const string DefaultBaseUrl = "https://api.bgm.tv";

        // Bangumi 官方要求非浏览器使用者携带「开发者ID + 应用名 + 版本 + 项目主页」形式的 User-Agent，并禁用请求库默认 UA。
        private const string UserAgentValue =
            "BLRQF/MediaInfoKeeper/1.7.5.4 (https://github.com/BLRQF/MediaInfoKeeper)";

        private const int RequestTimeoutMs = 8000;
        private const int ErrorCooldownSeconds = 60;
        private const int SubjectTypeAnime = 2;

        private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(250);
        private static readonly SemaphoreSlim Throttle = new(2, 2);
        private static readonly object GateLock = new();

        private static DateTime lastRequestUtc = DateTime.MinValue;
        private static DateTime cooldownUntilUtc = DateTime.MinValue;

        private static readonly JsonSerializerOptions JsonOptions = new() {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string ResolveBaseUrl() {
            var configured = Plugin.Instance?.Options?.MetaData?.ScrapersEditor?.Bangumi?.BaseUrl?.Trim();
            return string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.TrimEnd('/');
        }

        /// <summary>
        /// 条目搜索走旧版接口而非 v0 的 POST /v0/search/subjects：实测 v0 搜索只认拉丁字母关键词，
        /// 中日文（如「氷菓」「进击的巨人」）一律返回空，而本功能的主力关键词正是日文原名与中文名。
        /// 旧版接口对中日文与拉丁字母都正常，且 responseGroup=large 会一并返回 rating，可省一次请求。
        /// </summary>
        public static Task<string> SearchSubjectsAsync(string keyword, int limit, CancellationToken cancellationToken) {
            if (string.IsNullOrWhiteSpace(keyword)) return Task.FromResult<string>(null);

            var url = ResolveBaseUrl() + "/search/subject/" + Uri.EscapeDataString(keyword) +
                      "?type=" + SubjectTypeAnime + "&responseGroup=large&max_results=" + limit;

            return SendAsync(url, "GET", null, cancellationToken);
        }

        public static Task<string> SearchCharactersAsync(string keyword, int limit, CancellationToken cancellationToken) {
            if (string.IsNullOrWhiteSpace(keyword)) return Task.FromResult<string>(null);

            var body = JsonSerializer.Serialize(new { keyword }, JsonOptions);

            return SendAsync(BuildUrl("/v0/search/characters", limit), "POST", body, cancellationToken);
        }

        public static Task<string> SearchPersonsAsync(string keyword, int limit, CancellationToken cancellationToken) {
            if (string.IsNullOrWhiteSpace(keyword)) return Task.FromResult<string>(null);

            var body = JsonSerializer.Serialize(new { keyword }, JsonOptions);

            return SendAsync(BuildUrl("/v0/search/persons", limit), "POST", body, cancellationToken);
        }

        public static Task<string> GetSubjectAsync(long subjectId, CancellationToken cancellationToken) {
            return SendAsync(BuildUrl("/v0/subjects/" + subjectId), "GET", null, cancellationToken);
        }

        public static Task<string> GetSubjectCharactersAsync(long subjectId, CancellationToken cancellationToken) {
            return SendAsync(BuildUrl("/v0/subjects/" + subjectId + "/characters"), "GET", null, cancellationToken);
        }

        public static Task<string> GetCharacterAsync(long characterId, CancellationToken cancellationToken) {
            return SendAsync(BuildUrl("/v0/characters/" + characterId), "GET", null, cancellationToken);
        }

        public static Task<string> GetPersonAsync(long personId, CancellationToken cancellationToken) {
            return SendAsync(BuildUrl("/v0/persons/" + personId), "GET", null, cancellationToken);
        }

        public static T Deserialize<T>(string body) where T : class {
            if (string.IsNullOrWhiteSpace(body)) return null;

            try {
                return JsonSerializer.Deserialize<T>(body, JsonOptions);
            }
            catch (Exception ex) {
                Plugin.SharedLogger?.Debug("BangumiApiClient 反序列化失败: type={0}, msg={1}", typeof(T).Name, ex.Message);
                return null;
            }
        }

        private static string BuildUrl(string path, int? limit = null) {
            var url = ResolveBaseUrl() + path;
            return limit.HasValue ? url + "?limit=" + limit.Value : url;
        }

        private static async Task<string> SendAsync(string url, string method, string jsonBody,
            CancellationToken cancellationToken) {
            var httpClient = Plugin.SharedHttpClient;
            if (httpClient == null || string.IsNullOrWhiteSpace(url)) return null;

            if (IsCoolingDown()) {
                Plugin.SharedLogger?.Debug("BangumiApiClient 冷却中，跳过请求: url={0}", url);
                return null;
            }

            await Throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {
                if (IsCoolingDown()) return null;

                await EnforceMinIntervalAsync(cancellationToken).ConfigureAwait(false);

                return await SendCoreAsync(httpClient, url, method, jsonBody, cancellationToken).ConfigureAwait(false);
            }
            finally {
                Throttle.Release();
            }
        }

        private static async Task<string> SendCoreAsync(IHttpClient httpClient, string url, string method, string jsonBody,
            CancellationToken cancellationToken) {
            try {
                var requestOptions = new HttpRequestOptions {
                    Url = url,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = UserAgentValue,
                    EnableDefaultUserAgent = false,
                    TimeoutMs = RequestTimeoutMs,
                    ThrowOnErrorResponse = false
                };

                if (!string.IsNullOrWhiteSpace(jsonBody)) {
                    requestOptions.RequestContent = jsonBody.AsMemory();
                    requestOptions.RequestContentType = "application/json";
                }

                var accessToken = Plugin.Instance?.Options?.MetaData?.ScrapersEditor?.Bangumi?.AccessToken?.Trim();
                if (!string.IsNullOrWhiteSpace(accessToken))
                    requestOptions.RequestHeaders["Authorization"] = "Bearer " + accessToken;

                using var response = await httpClient.SendAsync(requestOptions, method).ConfigureAwait(false);
                var body = await ReadResponseBodyAsync(response).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;

                if (statusCode == 429) {
                    EnterCooldown();
                    Plugin.SharedLogger?.Debug("BangumiApiClient 触发限流，冷却 {0}s: url={1}", ErrorCooldownSeconds, url);
                    return null;
                }

                if (statusCode < 200 || statusCode >= 300) {
                    Plugin.SharedLogger?.Debug("BangumiApiClient 请求失败: status={0}, url={1}", statusCode, url);
                    return null;
                }

                return body;
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception ex) {
                Plugin.SharedLogger?.Debug("BangumiApiClient 请求异常: url={0}, msg={1}", url, ex.Message);
                return null;
            }
        }

        private static async Task<string> ReadResponseBodyAsync(HttpResponseInfo response) {
            if (response?.Content == null) return null;

            using var reader = new StreamReader(response.Content, Encoding.UTF8);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        private static bool IsCoolingDown() {
            lock (GateLock) return DateTime.UtcNow < cooldownUntilUtc;
        }

        private static void EnterCooldown() {
            lock (GateLock) cooldownUntilUtc = DateTime.UtcNow.AddSeconds(ErrorCooldownSeconds);
        }

        private static async Task EnforceMinIntervalAsync(CancellationToken cancellationToken) {
            TimeSpan delay;
            lock (GateLock) {
                var now = DateTime.UtcNow;
                var sinceLast = now - lastRequestUtc;
                delay = sinceLast < MinRequestInterval ? MinRequestInterval - sinceLast : TimeSpan.Zero;
                lastRequestUtc = now + delay;
            }

            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    internal sealed class BangumiPagedResult<T> {
        public List<T> Data { get; set; }
    }

    /// <summary>旧版 /search/subject/{keyword} 的响应包装。</summary>
    internal sealed class BangumiLegacySearchResponse {
        public int Results { get; set; }

        public List<BangumiLegacySubject> List { get; set; }
    }

    /// <summary>
    /// 旧版搜索的条目结构。与 v0 的差异：用 air_date 而非 date，没有 platform 与 infobox，但带 rating。
    /// </summary>
    internal sealed class BangumiLegacySubject {
        public long Id { get; set; }

        public string Name { get; set; }

        [JsonPropertyName("name_cn")]
        public string NameCn { get; set; }

        [JsonPropertyName("air_date")]
        public string AirDate { get; set; }

        public int Eps { get; set; }

        public int Type { get; set; }

        public BangumiRating Rating { get; set; }

        public BangumiSearchSubject ToSearchSubject() {
            return new BangumiSearchSubject {
                Id = Id,
                Name = Name,
                NameCn = NameCn,
                Date = AirDate,
                Eps = Eps,
                Rating = Rating
            };
        }
    }

    internal sealed class BangumiSearchSubject {
        public long Id { get; set; }

        public string Name { get; set; }

        [JsonPropertyName("name_cn")]
        public string NameCn { get; set; }

        public string Date { get; set; }

        public int Eps { get; set; }

        [JsonPropertyName("total_episodes")]
        public int TotalEpisodes { get; set; }

        public string Platform { get; set; }

        public BangumiRating Rating { get; set; }

        public List<BangumiInfoboxItem> Infobox { get; set; }

        public int? Year => ParseYear(Date);

        internal static int? ParseYear(string date) {
            if (string.IsNullOrWhiteSpace(date) || date.Length < 4) return null;

            return int.TryParse(date.Substring(0, 4), out var year) ? year : null;
        }
    }

    internal sealed class BangumiSubject {
        public long Id { get; set; }

        public string Name { get; set; }

        [JsonPropertyName("name_cn")]
        public string NameCn { get; set; }

        public string Date { get; set; }

        public int Eps { get; set; }

        public string Platform { get; set; }

        public BangumiRating Rating { get; set; }
    }

    internal sealed class BangumiRating {
        public int Rank { get; set; }

        public int Total { get; set; }

        public double Score { get; set; }
    }

    internal sealed class BangumiRelatedCharacter {
        public long Id { get; set; }

        public string Name { get; set; }

        public int Type { get; set; }

        public string Relation { get; set; }

        public List<BangumiPerson> Actors { get; set; }
    }

    internal sealed class BangumiPerson {
        public long Id { get; set; }

        public string Name { get; set; }

        public List<string> Career { get; set; }

        public int Type { get; set; }

        public List<BangumiInfoboxItem> Infobox { get; set; }
    }

    internal sealed class BangumiCharacterDetail {
        public long Id { get; set; }

        public string Name { get; set; }

        public List<BangumiInfoboxItem> Infobox { get; set; }

        // 仅用于磁盘缓存条目：API 本身不返回该字段（中文名在 infobox 里）。
        public string ChineseName { get; set; }
    }

    internal sealed class BangumiPersonDetail {
        public long Id { get; set; }

        public string Name { get; set; }

        public List<BangumiInfoboxItem> Infobox { get; set; }
    }

    internal sealed class BangumiInfoboxItem {
        public string Key { get; set; }

        public JsonElement Value { get; set; }
    }

    internal static class BangumiInfobox {
        public static readonly string[] ChineseNameKeys = { "简体中文名", "中文名" };

        public static string FirstChineseName(IEnumerable<BangumiInfoboxItem> infobox) {
            return First(infobox, ChineseNameKeys);
        }

        public static string First(IEnumerable<BangumiInfoboxItem> infobox, params string[] keys) {
            if (infobox == null || keys == null) return null;

            foreach (var item in infobox) {
                if (item == null || string.IsNullOrWhiteSpace(item.Key)) continue;

                if (!keys.Any(key => string.Equals(key, item.Key.Trim(), StringComparison.Ordinal))) continue;

                var value = ExtractValues(item.Value).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }

            return null;
        }

        public static List<string> All(IEnumerable<BangumiInfoboxItem> infobox, params string[] keys) {
            var results = new List<string>();
            if (infobox == null || keys == null) return results;

            foreach (var item in infobox) {
                if (item == null || string.IsNullOrWhiteSpace(item.Key)) continue;

                if (!keys.Any(key => string.Equals(key, item.Key.Trim(), StringComparison.Ordinal))) continue;

                foreach (var value in ExtractValues(item.Value))
                    if (!string.IsNullOrWhiteSpace(value) && !results.Contains(value))
                        results.Add(value);
            }

            return results;
        }

        // infobox 的 value 是 string 与 [{k,v}] 的联合类型，必须按 JsonElement 的实际种类分派。
        private static IEnumerable<string> ExtractValues(JsonElement value) {
            if (value.ValueKind == JsonValueKind.String) {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) yield return text.Trim();
                yield break;
            }

            if (value.ValueKind != JsonValueKind.Array) yield break;

            foreach (var element in value.EnumerateArray()) {
                if (element.ValueKind == JsonValueKind.String) {
                    var text = element.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) yield return text.Trim();
                    continue;
                }

                if (element.ValueKind != JsonValueKind.Object) continue;
                if (!element.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.String) continue;

                var item = v.GetString();
                if (!string.IsNullOrWhiteSpace(item)) yield return item.Trim();
            }
        }
    }
}
