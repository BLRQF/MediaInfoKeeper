using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaInfoKeeper.Options;

namespace MediaInfoKeeper.Services {
    /// <summary>
    /// 单个条目的 Bangumi 增强流程。计划任务与右键菜单共用这一份实现。
    /// </summary>
    internal static class BangumiRunner {
        public static async Task<BangumiRunOutcome> RunItemAsync(BaseItem item, MetaDataOptions options,
            CancellationToken cancellationToken) {
            var outcome = new BangumiRunOutcome();
            if (item == null || options == null) return outcome;

            var budget = new BangumiRequestBudget(BangumiService.PerItemRequestBudget);

            var subjectId = await BangumiService.ResolveSubjectIdAsync(item, budget, cancellationToken)
                .ConfigureAwait(false);
            if (subjectId <= 0) {
                Plugin.SharedLogger?.Debug("Bangumi 未解析到条目 item={0}", item.Name ?? string.Empty);
                return outcome;
            }

            var rating = options.EnableBangumiRating
                ? await BangumiService.GetRatingAsync(subjectId, budget, cancellationToken).ConfigureAwait(false)
                : null;

            if (options.EnableBangumiNfoWrite) {
                var nfoResult = await NfoWriter.EnhanceAsync(
                        item,
                        subjectId,
                        rating,
                        options.EnableBangumiCharacters,
                        options.EnableBangumiRating,
                        options.EnableBangumiSkipExistingChineseRole,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (nfoResult.Written) {
                    outcome.NfoWritten = true;
                    Plugin.SharedLogger?.Info("Bangumi 已写入 nfo: {0}, 角色 {1} 个, 评分 {2}", nfoResult.Path,
                        nfoResult.RolesChanged, nfoResult.RatingChanged);
                }
                else if (nfoResult.SkipReason != "no-change") {
                    Plugin.SharedLogger?.Debug("Bangumi 跳过 nfo: item={0}, reason={1}", item.Name ?? string.Empty,
                        nfoResult.SkipReason ?? string.Empty);
                }
            }

            // Emby 数据库侧：借道一次收窄的刷新，由 BangumiProvider 写入角色名与评分。
            // 此时解析结果已进耐久层，Provider 侧不会再产生网络请求。
            await MetaDataRunner.RefreshMetaDataAsync(item.InternalId, BuildRefreshOptions(), cancellationToken,
                RefreshPriority.High).ConfigureAwait(false);

            outcome.Processed = true;
            return outcome;
        }

        /// <summary>
        /// 把一批条目归并成待处理的剧集/电影。范围查询与 ExpandItem 返回的都是有路径的视频条目
        /// （电影与分集），剧集需要向上归并 —— 与 RefreshRecentMetadataTask 的做法一致。
        /// </summary>
        public static List<BaseItem> CollectTargets(IEnumerable<BaseItem> items) {
            var targets = new List<BaseItem>();
            var seen = new HashSet<long>();
            if (items == null) return targets;

            foreach (var item in items) {
                var target = item switch {
                    Movie movie => movie,
                    Series series => series,
                    Season season => season.Series,
                    Episode episode => episode.Series,
                    _ => null
                };

                if (target == null || target.InternalId <= 0) continue;

                if (seen.Add(target.InternalId)) targets.Add(target);
            }

            return targets;
        }

        /// <summary>
        /// 刻意不用 MetaDataRunner 的默认选项（它带 ReplaceAllMetadata / ReplaceAllImages = true）。
        /// 这里只需要 Provider 跑一遍：ReplaceAllMetadata=false 保证只补空不覆盖，
        /// ImageRefreshMode 取 ValidationOnly 避免重下图片（仓库有过递归刷新误清理剧集图片的教训）。
        /// </summary>
        private static MetadataRefreshOptions BuildRefreshOptions() {
            return new MetadataRefreshOptions(new DirectoryService(Plugin.SharedLogger, Plugin.FileSystem)) {
                EnableRemoteContentProbe = false,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false,
                Recursive = false,
                IsAutomated = true
            };
        }
    }

    internal sealed class BangumiRunOutcome {
        public bool Processed { get; set; }

        public bool NfoWritten { get; set; }
    }
}
