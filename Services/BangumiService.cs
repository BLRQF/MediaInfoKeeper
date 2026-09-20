using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaInfoKeeper.Common;
using MediaInfoKeeper.External;

namespace MediaInfoKeeper.Services {
    internal static class BangumiService {
        private const string SubjectSearchCacheScope = "bangumi-subject-search";
        private const string CharacterIndexCacheScope = "bangumi-char-index";
        private const string CharacterDetailCacheScope = "bangumi-char-detail";
        private const string CharacterSearchCacheScope = "bangumi-char-search";
        private const string PersonSearchCacheScope = "bangumi-person-search";

        private static readonly TimeSpan SearchCacheDuration = TimeSpan.FromHours(24);
        private static readonly TimeSpan CharacterIndexCacheDuration = TimeSpan.FromHours(24);
        private static readonly TimeSpan DetailCacheDuration = TimeSpan.FromDays(7);

        /// <summary>单个条目的请求软上限。超预算的角色留待下次运行，缓存单调前进会收敛。</summary>
        internal const int PerItemRequestBudget = 25;

        private const double MinConfidence = 0.80;
        private const double AmbiguityEpsilon = 0.001;
        private const double MinSubjectScore = 4;
        private const double MinSubjectScoreMargin = 2;
        private const int SubjectSearchLimit = 20;
        private const int BridgeSearchLimit = 10;
        private const int BridgeCandidateDepth = 5;

        private static readonly Regex CjkRegex = new(@"[一-鿿]", RegexOptions.Compiled);
        private static readonly Regex KanaRegex = new(@"[぀-ヿ]", RegexOptions.Compiled);

        private static readonly char[] NameSeparators = { ' ', '·', '・', '.', '-', '_', '/', '／' };
        private static readonly string[] RoleSuffixes = { "(voice)", "(Voice)", "（voice）", "（Voice）" };
        private static readonly char[] RoleSuffixStarters = { '(', '（' };

        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public static async Task<long> ResolveSubjectIdAsync(BaseItem item, BangumiRequestBudget budget,
            CancellationToken cancellationToken) {
            if (item == null) return 0;

            var stored = item.GetProviderId(BangumiExternalId.StaticName);
            if (long.TryParse(stored?.Trim(), out var existing) && existing > 0) return existing;

            var tmdbKey = item.GetProviderId(MetadataProviders.Tmdb);
            if (!string.IsNullOrWhiteSpace(tmdbKey) && BangumiStore.TryGetSubjectId(tmdbKey, out var learned))
                return ApplySubjectId(item, tmdbKey, learned);

            foreach (var keyword in BuildSearchKeywords(item)) {
                var subjectId = await SearchSubjectByKeywordAsync(item, keyword, budget, cancellationToken)
                    .ConfigureAwait(false);
                if (subjectId > 0) return ApplySubjectId(item, tmdbKey, subjectId);
            }

            return 0;
        }

        public static async Task<BangumiStoredRating> GetRatingAsync(long subjectId, BangumiRequestBudget budget,
            CancellationToken cancellationToken) {
            if (subjectId <= 0) return null;

            var learned = BangumiStore.GetRating(subjectId);
            if (learned != null && learned.Score > 0) return learned;

            if (!budget.TryConsume()) return null;

            var body = await BangumiApiClient.GetSubjectAsync(subjectId, cancellationToken).ConfigureAwait(false);
            var subject = BangumiApiClient.Deserialize<BangumiSubject>(body);
            if (subject?.Rating == null || subject.Rating.Score <= 0) return null;

            var rating = new BangumiStoredRating {
                Score = subject.Rating.Score,
                Total = subject.Rating.Total
            };
            BangumiStore.SetRating(subjectId, rating);
            return rating;
        }

        /// <summary>
        /// 把一组「演员/角色」查询解析为 Bangumi 中文角色名。Emby 的 PersonInfo 与 nfo 的 actor 各自做适配器，匹配逻辑只此一份。
        /// </summary>
        public static async Task<List<BangumiRoleResult>> MatchRolesAsync(long subjectId,
            List<BangumiRoleQuery> queries, BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var results = new List<BangumiRoleResult>();
            if (subjectId <= 0 || queries == null || queries.Count == 0) return results;

            var index = await GetCharacterIndexAsync(subjectId, budget, cancellationToken).ConfigureAwait(false);
            if (index == null || index.Characters.Count == 0) return results;

            foreach (var query in queries) {
                var result = new BangumiRoleResult { Query = query };

                var existingChinese = query.ExistingChineseRole;
                if (!string.IsNullOrWhiteSpace(existingChinese)) {
                    result.ChineseName = existingChinese;
                    result.Rule = "existing";
                    results.Add(result);
                    continue;
                }

                var personKey = query.PersonKey;
                var cached = BangumiStore.GetRoleName(subjectId, personKey);
                if (!string.IsNullOrWhiteSpace(cached)) {
                    result.ChineseName = cached;
                    result.Rule = "store";
                    results.Add(result);
                    continue;
                }

                var match = await MatchQueryAsync(query, index, budget, cancellationToken).ConfigureAwait(false);
                if (match == null) {
                    result.SkipReason = "unmatched";
                    results.Add(result);
                    continue;
                }

                if (match.Confidence < MinConfidence) {
                    result.SkipReason = "low-confidence";
                    results.Add(result);
                    continue;
                }

                var chineseName = match.ChineseName;
                if (string.IsNullOrWhiteSpace(chineseName))
                    chineseName = await ResolveCharacterChineseNameAsync(match.CharacterId, budget, cancellationToken)
                        .ConfigureAwait(false);

                if (!IsUsableChineseName(chineseName)) {
                    result.SkipReason = "no-chinese-name";
                    results.Add(result);
                    continue;
                }

                result.ChineseName = chineseName;
                result.Rule = match.Rule;
                result.CharacterId = match.CharacterId;
                BangumiStore.SetRoleName(subjectId, personKey, chineseName);
                results.Add(result);
            }

            return results;
        }

        public static async Task<BangumiEnhanceResult> EnhancePeopleRoleAsync(long subjectId, List<PersonInfo> people,
            BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var summary = new BangumiEnhanceResult();
            if (people == null || people.Count == 0) return summary;

            var targets = people.Where(ShouldHandlePerson).ToList();
            if (targets.Count == 0) return summary;

            var queries = targets.Select(person => new BangumiRoleQuery {
                TmdbId = person.GetProviderId(MetadataProviders.Tmdb),
                PersonName = person.Name,
                RoleName = person.Role
            }).ToList();

            var results = await MatchRolesAsync(subjectId, queries, budget, cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < results.Count && i < targets.Count; i++) {
                var result = results[i];
                if (!string.IsNullOrWhiteSpace(result.SkipReason)) {
                    if (result.SkipReason == "low-confidence") summary.LowConfidence++;
                    else if (result.SkipReason == "no-chinese-name") summary.NoChineseName++;
                    else summary.Unmatched++;
                    continue;
                }

                summary.Matched++;
                var person = targets[i];
                if (string.Equals(person.Role, result.ChineseName, StringComparison.Ordinal)) continue;

                person.Role = result.ChineseName;
                summary.Changed++;
                Plugin.SharedLogger?.Debug("BangumiService 角色名已更新: subject={0}, person={1}, rule={2}, role={3}",
                    subjectId, person.Name ?? string.Empty, result.Rule, result.ChineseName);
            }

            return summary;
        }

        private static long ApplySubjectId(BaseItem item, string tmdbKey, long subjectId) {
            if (subjectId <= 0) return 0;

            item.SetProviderId(BangumiExternalId.StaticName, subjectId.ToString());
            if (!string.IsNullOrWhiteSpace(tmdbKey)) BangumiStore.SetSubjectId(tmdbKey, subjectId);

            return subjectId;
        }

        private static IEnumerable<string> BuildSearchKeywords(BaseItem item) {
            var keywords = new List<string>();

            void Add(string value) {
                var trimmed = value?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && !keywords.Contains(trimmed)) keywords.Add(trimmed);
            }

            Add(item.OriginalTitle);
            Add(item.Name);

            return keywords;
        }

        private static async Task<long> SearchSubjectByKeywordAsync(BaseItem item, string keyword,
            BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var candidates = await SearchSubjectsAsync(keyword, budget, cancellationToken).ConfigureAwait(false);
            if (candidates == null || candidates.Count == 0) return 0;

            var scored = candidates
                .Select(candidate => new { Candidate = candidate, Score = ScoreSubject(item, candidate) })
                .OrderByDescending(entry => entry.Score)
                .ToList();

            var best = scored[0];
            if (best.Score < MinSubjectScore) {
                Plugin.SharedLogger?.Debug("BangumiService 条目得分不足: keyword={0}, best={1}, score={2}", keyword,
                    best.Candidate?.Name ?? string.Empty, best.Score);
                return 0;
            }

            if (scored.Count > 1 && best.Score - scored[1].Score < MinSubjectScoreMargin) {
                Plugin.SharedLogger?.Debug("BangumiService 条目得分无优势: keyword={0}, best={1}({2}), second={3}({4})",
                    keyword, best.Candidate?.Name ?? string.Empty, best.Score,
                    scored[1].Candidate?.Name ?? string.Empty, scored[1].Score);
                return 0;
            }

            var bestId = best.Candidate?.Id ?? 0;

            // 旧版搜索的 responseGroup=large 已带 rating，顺手入耐久层，GetRatingAsync 就无需再请求一次。
            if (bestId > 0 && best.Candidate?.Rating != null && best.Candidate.Rating.Score > 0)
                BangumiStore.SetRating(bestId, new BangumiStoredRating {
                    Score = best.Candidate.Rating.Score,
                    Total = best.Candidate.Rating.Total
                });

            return bestId;
        }

        private static double ScoreSubject(BaseItem item, BangumiSearchSubject candidate) {
            if (candidate == null) return 0;

            var score = 0d;
            var originalKey = NormalizeName(item.OriginalTitle);
            var nameKey = NormalizeName(item.Name);
            var candidateNameKey = NormalizeName(candidate.Name);
            var candidateNameCnKey = NormalizeName(candidate.NameCn);

            if (originalKey.Length > 0 &&
                (originalKey == candidateNameKey || originalKey == candidateNameCnKey)) score += 3;
            else if (nameKey.Length > 0 &&
                     (nameKey == candidateNameKey || nameKey == candidateNameCnKey)) score += 3;
            else if (nameKey.Length > 0 && BangumiInfobox.All(candidate.Infobox, "别名")
                         .Any(alias => NormalizeName(alias) == nameKey)) score += 2;

            var year = candidate.Year;
            if (item.ProductionYear.HasValue && year.HasValue) {
                var diff = Math.Abs(item.ProductionYear.Value - year.Value);
                if (diff == 0) score += 3;
                else if (diff == 1) score += 2;
            }

            var platform = candidate.Platform?.Trim();
            if (!string.IsNullOrWhiteSpace(platform) &&
                (platform.IndexOf("TV", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 platform.IndexOf("Web", StringComparison.OrdinalIgnoreCase) >= 0)) score += 1;

            return score;
        }

        private static async Task<List<BangumiSearchSubject>> SearchSubjectsAsync(string keyword,
            BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var cached = PluginDiskCache.GetJson<List<BangumiSearchSubject>>(SubjectSearchCacheScope, keyword,
                SearchCacheDuration, JsonOptions);
            if (cached != null) return cached;

            if (!budget.TryConsume()) return null;

            var body = await BangumiApiClient.SearchSubjectsAsync(keyword, SubjectSearchLimit, cancellationToken)
                .ConfigureAwait(false);
            var legacy = BangumiApiClient.Deserialize<BangumiLegacySearchResponse>(body);
            var candidates = legacy?.List?
                .Where(subject => subject != null && subject.Id > 0)
                .Select(subject => subject.ToSearchSubject())
                .ToList();
            if (candidates == null) return null;

            PluginDiskCache.SetJson(SubjectSearchCacheScope, keyword, candidates, JsonOptions);
            return candidates;
        }

        private static async Task<BangumiCharacterIndex> GetCharacterIndexAsync(long subjectId,
            BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var cached = PluginDiskCache.GetJson<BangumiCharacterIndex>(CharacterIndexCacheScope, subjectId.ToString(),
                CharacterIndexCacheDuration, JsonOptions);
            if (cached != null) return cached;

            if (!budget.TryConsume()) return null;

            var body = await BangumiApiClient.GetSubjectCharactersAsync(subjectId, cancellationToken)
                .ConfigureAwait(false);
            var raw = ParseList<BangumiRelatedCharacter>(body);
            if (raw == null) return null;

            var index = new BangumiCharacterIndex {
                SubjectId = subjectId,
                Characters = raw
                    .Where(character => character != null && character.Id > 0)
                    .Select(character => new BangumiIndexedCharacter {
                        Id = character.Id,
                        Name = character.Name,
                        Type = character.Type,
                        Relation = character.Relation,
                        Actors = (character.Actors ?? new List<BangumiPerson>())
                            .Where(actor => actor != null && !string.IsNullOrWhiteSpace(actor.Name))
                            .Select(actor => new BangumiIndexedActor { Id = actor.Id, Name = actor.Name })
                            .ToList()
                    })
                    .ToList()
            };

            PluginDiskCache.SetJson(CharacterIndexCacheScope, subjectId.ToString(), index, JsonOptions);
            return index;
        }

        private static async Task<BangumiRoleMatch> MatchQueryAsync(BangumiRoleQuery query,
            BangumiCharacterIndex index, BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var local = MatchLocal(query, index);
            if (local != null) return local;

            var byPerson = await MatchByPersonSearchAsync(query, index, budget, cancellationToken).ConfigureAwait(false);
            if (byPerson != null) return byPerson;

            return await MatchByCharacterSearchAsync(query, index, budget, cancellationToken).ConfigureAwait(false);
        }

        private static BangumiRoleMatch MatchLocal(BangumiRoleQuery query, BangumiCharacterIndex index) {
            var roleKey = NormalizeName(StripRoleSuffix(query.RoleName));
            var actorKey = NormalizeName(query.PersonName);
            var roleTokens = TokenSet(StripRoleSuffix(query.RoleName));
            var actorTokens = TokenSet(query.PersonName);

            var scored = new List<BangumiRoleMatch>();

            foreach (var character in index.Characters) {
                if (character.Type != 1) continue;

                var characterKey = NormalizeName(character.Name);
                var characterTokens = TokenSet(character.Name);

                var roleExact = roleKey.Length > 0 && roleKey == characterKey;
                var roleToken = !roleExact && roleTokens.Count > 0 && roleTokens.SetEquals(characterTokens);

                var actorExact = actorKey.Length > 0 && character.Actors.Any(actor =>
                    NormalizeName(actor.Name) == actorKey);
                var actorToken = !actorExact && actorTokens.Count > 0 && character.Actors.Any(actor =>
                    TokenSet(actor.Name).SetEquals(actorTokens));

                var hasRole = roleExact || roleToken;
                var hasActor = actorExact || actorToken;
                if (!hasRole && !hasActor) continue;

                double confidence;
                string rule;
                if (roleExact) {
                    confidence = 1.00;
                    rule = hasActor ? "K1+K2" : "K1";
                }
                else if (roleToken) {
                    confidence = 0.95;
                    rule = hasActor ? "K3+K2" : "K3";
                }
                else if (actorExact) {
                    confidence = 0.95;
                    rule = "K2";
                }
                else {
                    confidence = 0.90;
                    rule = "K3";
                }

                scored.Add(new BangumiRoleMatch {
                    CharacterId = character.Id,
                    Confidence = confidence,
                    Rule = rule
                });
            }

            return PickUnique(scored, "local");
        }

        private static async Task<BangumiRoleMatch> MatchByPersonSearchAsync(BangumiRoleQuery query,
            BangumiCharacterIndex index, BangumiRequestBudget budget, CancellationToken cancellationToken) {
            if (string.IsNullOrWhiteSpace(query.PersonName)) return null;

            var hits = await SearchPersonsAsync(query.PersonName, budget, cancellationToken).ConfigureAwait(false);
            if (hits == null || hits.Count == 0) return null;

            var hitIds = new HashSet<long>(hits.Take(BridgeCandidateDepth)
                .Where(hit => hit != null && hit.Id > 0)
                .Select(hit => hit.Id));

            if (hitIds.Count == 0) return null;

            // 关键不变量：候选角色必须存在于该条目的角色索引中，否则会匹配到别的作品。
            var candidates = index.Characters
                .Where(character => character.Type == 1 && character.Actors.Any(actor => hitIds.Contains(actor.Id)))
                .Select(character => new BangumiRoleMatch {
                    CharacterId = character.Id,
                    Confidence = 0.90,
                    Rule = "K4"
                })
                .ToList();

            return PickUnique(candidates, "person-search");
        }

        private static async Task<BangumiRoleMatch> MatchByCharacterSearchAsync(BangumiRoleQuery query,
            BangumiCharacterIndex index, BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var role = StripRoleSuffix(query.RoleName);
            if (string.IsNullOrWhiteSpace(role)) return null;

            var hits = await SearchCharactersAsync(role, budget, cancellationToken).ConfigureAwait(false);
            if (hits == null || hits.Count == 0) return null;

            var indexIds = new HashSet<long>(index.Characters.Where(character => character.Type == 1)
                .Select(character => character.Id));

            var candidates = hits.Take(BridgeCandidateDepth)
                .Where(hit => hit != null && indexIds.Contains(hit.Id))
                .Select(hit => new BangumiRoleMatch {
                    CharacterId = hit.Id,
                    Confidence = 0.85,
                    Rule = "K5",
                    ChineseName = BangumiInfobox.FirstChineseName(hit.Infobox)
                })
                .ToList();

            return PickUnique(candidates, "character-search");
        }

        private static BangumiRoleMatch PickUnique(List<BangumiRoleMatch> candidates, string source) {
            if (candidates == null || candidates.Count == 0) return null;

            var best = candidates.Max(candidate => candidate.Confidence);
            var top = candidates.Where(candidate => Math.Abs(candidate.Confidence - best) < AmbiguityEpsilon).ToList();

            if (top.Count != 1) {
                Plugin.SharedLogger?.Debug("BangumiService 匹配有歧义: source={0}, 候选数={1}, confidence={2}", source,
                    top.Count, best);
                return null;
            }

            return top[0];
        }

        internal static async Task<string> ResolveCharacterChineseNameAsync(long characterId,
            BangumiRequestBudget budget, CancellationToken cancellationToken) {
            if (characterId <= 0) return null;

            var cacheKey = characterId.ToString();
            var cached = PluginDiskCache.GetJson<BangumiCharacterDetail>(CharacterDetailCacheScope, cacheKey,
                DetailCacheDuration, JsonOptions);
            if (cached != null) return cached.ChineseName;

            if (!budget.TryConsume()) return null;

            var body = await BangumiApiClient.GetCharacterAsync(characterId, cancellationToken).ConfigureAwait(false);
            var detail = BangumiApiClient.Deserialize<BangumiCharacterDetail>(body);
            if (detail == null) return null;

            var chineseName = BangumiInfobox.FirstChineseName(detail.Infobox);
            if (!IsUsableChineseName(chineseName) ||
                string.Equals(chineseName, detail.Name, StringComparison.Ordinal)) chineseName = null;

            PluginDiskCache.SetJson(CharacterDetailCacheScope, cacheKey, new BangumiCharacterDetail {
                Id = detail.Id,
                Name = detail.Name,
                ChineseName = chineseName
            }, JsonOptions);

            return chineseName;
        }

        private static async Task<List<BangumiCharacterDetail>> SearchCharactersAsync(string keyword,
            BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var cached = PluginDiskCache.GetJson<List<BangumiCharacterDetail>>(CharacterSearchCacheScope, keyword,
                SearchCacheDuration, JsonOptions);
            if (cached != null) return cached;

            if (!budget.TryConsume()) return null;

            var body = await BangumiApiClient.SearchCharactersAsync(keyword, BridgeSearchLimit, cancellationToken)
                .ConfigureAwait(false);
            var hits = ParseList<BangumiCharacterDetail>(body);
            if (hits == null) return null;

            PluginDiskCache.SetJson(CharacterSearchCacheScope, keyword, hits, JsonOptions);
            return hits;
        }

        private static async Task<List<BangumiPersonDetail>> SearchPersonsAsync(string keyword,
            BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var cached = PluginDiskCache.GetJson<List<BangumiPersonDetail>>(PersonSearchCacheScope, keyword,
                SearchCacheDuration, JsonOptions);
            if (cached != null) return cached;

            if (!budget.TryConsume()) return null;

            var body = await BangumiApiClient.SearchPersonsAsync(keyword, BridgeSearchLimit, cancellationToken)
                .ConfigureAwait(false);
            var hits = ParseList<BangumiPersonDetail>(body);
            if (hits == null) return null;

            PluginDiskCache.SetJson(PersonSearchCacheScope, keyword, hits, JsonOptions);
            return hits;
        }

        private static List<T> ParseList<T>(string body) where T : class {
            if (string.IsNullOrWhiteSpace(body)) return null;

            var trimmed = body.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal)) return BangumiApiClient.Deserialize<List<T>>(body);

            return BangumiApiClient.Deserialize<BangumiPagedResult<T>>(body)?.Data;
        }

        private static bool ShouldHandlePerson(PersonInfo person) {
            return person != null &&
                   (person.Type == PersonType.Actor || person.Type == PersonType.GuestStar);
        }

        internal static string StripRoleSuffix(string role) {
            if (string.IsNullOrWhiteSpace(role)) return role;

            var trimmed = role.Trim();
            foreach (var suffix in RoleSuffixes)
                if (trimmed.EndsWith(suffix, StringComparison.Ordinal))
                    return trimmed.Substring(0, trimmed.Length - suffix.Length).Trim();

            return trimmed;
        }

        /// <summary>
        /// 判断角色名是否已经是中文。刻意不复用 LanguageUtility.IsChinese —— 那个方法带有一处「・」特例，
        /// 语义是为剧集名称设计的。
        /// </summary>
        internal static bool IsProbablyChineseRole(string roleName) {
            var value = StripRoleSuffix(roleName);
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (!CjkRegex.IsMatch(value)) return false;

            return !KanaRegex.IsMatch(value.Replace("・", string.Empty));
        }

        internal static bool IsUsableChineseName(string value) {
            return IsProbablyChineseRole(value);
        }

        internal static string NormalizeName(string value) {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var builder = new StringBuilder(value.Length);
            foreach (var ch in value) {
                if (char.IsWhiteSpace(ch)) continue;
                if (ch == '·' || ch == '・' || ch == '.') continue;

                builder.Append(char.ToLowerInvariant(ch));
            }

            return builder.ToString();
        }

        internal static string ComposeRoleText(string originalRole, string chineseName) {
            if (string.IsNullOrWhiteSpace(originalRole) || string.IsNullOrWhiteSpace(chineseName)) return chineseName;

            var index = originalRole.LastIndexOfAny(RoleSuffixStarters);
            if (index <= 0) return chineseName;

            var suffix = originalRole.Substring(index).Trim();
            return string.IsNullOrWhiteSpace(suffix) ? chineseName : chineseName + " " + suffix;
        }

        private static HashSet<string> TokenSet(string value) {
            if (string.IsNullOrWhiteSpace(value)) return new HashSet<string>(StringComparer.Ordinal);

            var tokens = value.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.ToLowerInvariant());

            return new HashSet<string>(tokens, StringComparer.Ordinal);
        }
    }

    internal sealed class BangumiRequestBudget {
        private int remaining;

        public BangumiRequestBudget(int remaining) {
            this.remaining = remaining;
        }

        public int Remaining => remaining;

        public bool TryConsume() {
            if (remaining <= 0) return false;

            remaining--;
            return true;
        }
    }

    internal sealed class BangumiRoleQuery {
        public string TmdbId { get; set; }

        public string PersonName { get; set; }

        public string RoleName { get; set; }

        /// <summary>开启「已是中文则跳过」时，由调用方填入现有中文名，服务直接原样返回。</summary>
        public string ExistingChineseRole { get; set; }

        public string PersonKey {
            get {
                if (!string.IsNullOrWhiteSpace(TmdbId)) return "tmdb:" + TmdbId.Trim();

                return "name:" + BangumiService.NormalizeName(PersonName);
            }
        }
    }

    internal sealed class BangumiRoleResult {
        public BangumiRoleQuery Query { get; set; }

        public long CharacterId { get; set; }

        public string ChineseName { get; set; }

        public string Rule { get; set; }

        public string SkipReason { get; set; }
    }

    internal sealed class BangumiEnhanceResult {
        public int Changed { get; set; }

        public int Matched { get; set; }

        public int Unmatched { get; set; }

        public int LowConfidence { get; set; }

        public int NoChineseName { get; set; }
    }

    internal sealed class BangumiRoleMatch {
        public long CharacterId { get; set; }

        public double Confidence { get; set; }

        public string Rule { get; set; }

        public string ChineseName { get; set; }
    }

    internal sealed class BangumiCharacterIndex {
        public long SubjectId { get; set; }

        public List<BangumiIndexedCharacter> Characters { get; set; } = new();
    }

    internal sealed class BangumiIndexedCharacter {
        public long Id { get; set; }

        public string Name { get; set; }

        public int Type { get; set; }

        public string Relation { get; set; }

        public List<BangumiIndexedActor> Actors { get; set; } = new();
    }

    internal sealed class BangumiIndexedActor {
        public long Id { get; set; }

        public string Name { get; set; }
    }
}
