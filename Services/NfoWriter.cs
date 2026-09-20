using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace MediaInfoKeeper.Services {
    /// <summary>
    /// 把 Bangumi 的中文角色名与评分非破坏性合并进 .nfo。
    /// 只改两处：actor 块里的 role 文本，以及 ratings 下新增/更新 name="bangumi" 的条目。
    /// 解析失败或结构不符时跳过该文件，绝不重写。
    /// </summary>
    internal static class NfoWriter {
        private const string BangumiRatingName = "bangumi";

        public static async Task<BangumiNfoWriteResult> EnhanceAsync(BaseItem item, long subjectId,
            BangumiStoredRating rating, bool enhanceCharacters, bool applyRating, bool skipExistingChineseRole,
            BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var result = new BangumiNfoWriteResult();
            if (item == null || subjectId <= 0) return result;

            var path = ResolveNfoPath(item);
            if (string.IsNullOrWhiteSpace(path)) {
                result.SkipReason = "nfo-not-found";
                return result;
            }

            result.Path = path;

            XDocument document;
            bool hadBom;
            try {
                document = LoadDocument(path, out hadBom);
            }
            catch (Exception ex) {
                result.SkipReason = "parse-failed";
                Plugin.SharedLogger?.Debug("NfoWriter 解析失败，跳过: path={0}, msg={1}", path, ex.Message);
                return result;
            }

            var root = document.Root;
            if (root == null) {
                result.SkipReason = "empty-document";
                return result;
            }

            var changed = false;

            if (enhanceCharacters) {
                var rolesChanged = await EnhanceActorsAsync(root, subjectId, skipExistingChineseRole, budget,
                    cancellationToken).ConfigureAwait(false);
                if (rolesChanged > 0) {
                    result.RolesChanged = rolesChanged;
                    changed = true;
                }
            }

            if (applyRating && rating != null && rating.Score > 0) {
                if (ApplyRating(root, rating)) {
                    result.RatingChanged = true;
                    changed = true;
                }
            }

            if (!changed) {
                result.SkipReason = "no-change";
                return result;
            }

            try {
                SaveDocument(path, document, hadBom);
                result.Written = true;
            }
            catch (Exception ex) {
                result.SkipReason = "write-failed";
                Plugin.SharedLogger?.Error("NfoWriter 写入失败: path={0}", path);
                Plugin.SharedLogger?.Error(ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
            }

            return result;
        }

        private static async Task<int> EnhanceActorsAsync(XElement root, long subjectId, bool skipExistingChineseRole,
            BangumiRequestBudget budget, CancellationToken cancellationToken) {
            var actors = root.Descendants("actor").ToList();
            if (actors.Count == 0) return 0;

            var entries = new List<ActorEntry>();
            foreach (var actor in actors) {
                var roleElement = actor.Element("role");
                if (roleElement == null) continue;

                entries.Add(new ActorEntry {
                    RoleElement = roleElement,
                    OriginalRole = roleElement.Value,
                    Query = new BangumiRoleQuery {
                        TmdbId = actor.Element("tmdbid")?.Value?.Trim(),
                        PersonName = actor.Element("name")?.Value?.Trim(),
                        RoleName = roleElement.Value
                    }
                });
            }

            if (entries.Count == 0) return 0;

            foreach (var entry in entries)
                if (skipExistingChineseRole && BangumiService.IsProbablyChineseRole(entry.OriginalRole))
                    entry.Query.ExistingChineseRole = entry.OriginalRole;

            var results = await BangumiService
                .MatchRolesAsync(subjectId, entries.Select(entry => entry.Query).ToList(), budget, cancellationToken)
                .ConfigureAwait(false);

            var changed = 0;
            for (var i = 0; i < results.Count && i < entries.Count; i++) {
                var chineseName = results[i].ChineseName;
                if (string.IsNullOrWhiteSpace(chineseName)) continue;

                var entry = entries[i];
                var newRole = BangumiService.ComposeRoleText(entry.OriginalRole, chineseName);
                if (string.Equals(newRole, entry.OriginalRole, StringComparison.Ordinal)) continue;

                entry.RoleElement.Value = newRole;
                changed++;
            }

            return changed;
        }

        private static bool ApplyRating(XElement root, BangumiStoredRating rating) {
            var ratings = root.Element("ratings");
            if (ratings == null) {
                // 结构不是预期的多来源形式，宁可不写也不猜。
                Plugin.SharedLogger?.Debug("NfoWriter 未找到 <ratings> 节点，跳过评分写入");
                return false;
            }

            var scoreText = rating.Score.ToString("0.0", CultureInfo.InvariantCulture);
            var votesText = rating.Total.ToString(CultureInfo.InvariantCulture);

            var entries = ratings.Elements("rating").ToList();
            var bangumi = entries.FirstOrDefault(IsBangumiRating);
            var changed = false;

            if (bangumi == null) {
                var template = entries.LastOrDefault();
                if (template == null) return false;

                var clone = new XElement(template);
                clone.SetAttributeValue("name", BangumiRatingName);
                clone.SetAttributeValue("max", "10");

                var clonedValue = clone.Element("value");
                if (clonedValue == null) return false;
                clonedValue.Value = scoreText;

                var clonedVotes = clone.Element("votes");
                if (clonedVotes != null) clonedVotes.Value = votesText;
                else clone.Add(new XElement("votes", votesText));

                // 克隆相邻条目以沿用原文件的缩进，避免整篇重排。
                var indent = (template.PreviousNode as XText)?.Value ?? Environment.NewLine + "    ";
                var trailing = ratings.Nodes().LastOrDefault() as XText;

                if (trailing != null) {
                    trailing.AddBeforeSelf(new XText(indent));
                    trailing.AddBeforeSelf(clone);
                }
                else {
                    ratings.Add(new XText(indent));
                    ratings.Add(clone);
                    ratings.Add(new XText(Environment.NewLine + "  "));
                }

                bangumi = clone;
                changed = true;
            }

            var valueElement = bangumi.Element("value");
            if (valueElement == null) return changed;
            if (!string.Equals(valueElement.Value, scoreText, StringComparison.Ordinal)) {
                valueElement.Value = scoreText;
                changed = true;
            }

            var votesElement = bangumi.Element("votes");
            if (votesElement != null && !string.Equals(votesElement.Value, votesText, StringComparison.Ordinal)) {
                votesElement.Value = votesText;
                changed = true;
            }

            // Emby 的 NFO 解析会读 rating 的 default 属性来挑选主评分，因此必须把 Bangumi 置为默认，
            // 否则媒体库里显示的还是 imdb / themoviedb 的分数。同时把原先的默认项降级，保持只有一个默认。
            if (SetDefaultFlag(bangumi, true)) changed = true;
            foreach (var element in entries.Where(element => !IsBangumiRating(element)))
                if (SetDefaultFlag(element, false)) changed = true;

            return changed;
        }

        private static bool IsBangumiRating(XElement element) {
            return string.Equals(element.Attribute("name")?.Value, BangumiRatingName,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool SetDefaultFlag(XElement element, bool isDefault) {
            var desired = isDefault ? "true" : "false";
            if (string.Equals(element.Attribute("default")?.Value, desired, StringComparison.OrdinalIgnoreCase))
                return false;

            element.SetAttributeValue("default", desired);
            return true;
        }

        private static string ResolveNfoPath(BaseItem item) {
            var path = item.Path;
            if (string.IsNullOrWhiteSpace(path)) return null;

            if (item is Series) {
                var seriesNfo = Path.Combine(path, "tvshow.nfo");
                return File.Exists(seriesNfo) ? seriesNfo : null;
            }

            // 电影（含 STRM 库）：优先与视频文件同名
            var byVideoName = Path.ChangeExtension(path, ".nfo");
            if (File.Exists(byVideoName)) return byVideoName;

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) return null;

            if (item is Movie) {
                var movieNfo = Path.Combine(directory, "movie.nfo");
                if (File.Exists(movieNfo)) return movieNfo;
            }

            return null;
        }

        private static XDocument LoadDocument(string path, out bool hadBom) {
            var bytes = File.ReadAllBytes(path);
            hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

            using var stream = new MemoryStream(bytes);
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }

        private static void SaveDocument(string path, XDocument document, bool hadBom) {
            var settings = new XmlWriterSettings {
                Encoding = new UTF8Encoding(hadBom),
                Indent = false,
                NewLineHandling = NewLineHandling.None,
                OmitXmlDeclaration = document.Declaration == null
            };

            using var writer = XmlWriter.Create(path, settings);
            document.Save(writer);
        }

        private sealed class ActorEntry {
            public XElement RoleElement { get; set; }

            public string OriginalRole { get; set; }

            public BangumiRoleQuery Query { get; set; }
        }
    }

    internal sealed class BangumiNfoWriteResult {
        public bool Written { get; set; }

        public string Path { get; set; }

        public int RolesChanged { get; set; }

        public bool RatingChanged { get; set; }

        public string SkipReason { get; set; }
    }
}
