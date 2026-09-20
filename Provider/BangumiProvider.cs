using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Provider {
    /// <summary>
    /// 用 Bangumi 的中文角色名与评分增强剧集/电影。
    /// Order 取 int.MaxValue - 10，排在 DoubanRoleProvider（int.MaxValue）之前：
    /// Bangumi 先写入中文角色名，豆瓣那条 IsChinese 守卫会自动礼让，形成「番剧以 Bangumi 为准，其余豆瓣兜底」。
    /// 评分是作品级的，不写单集。
    /// </summary>
    public sealed class BangumiProvider :
        ICustomMetadataProvider<Movie>,
        ICustomMetadataProvider<Series>,
        IForcedProvider,
        IHasOrder {
        public const string ProviderName = "Bangumi";

        public string Name => ProviderName;

        public int Order => int.MaxValue - 10;

        public async Task<ItemUpdateType> FetchAsync(
            MetadataResult<Movie> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken) {
            return await EnhanceAsync(itemResult?.Item, itemResult?.People, libraryOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<ItemUpdateType> FetchAsync(
            MetadataResult<Series> itemResult,
            MetadataRefreshOptions options,
            LibraryOptions libraryOptions,
            CancellationToken cancellationToken) {
            return await EnhanceAsync(itemResult?.Item, itemResult?.People, libraryOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task<ItemUpdateType> EnhanceAsync(BaseItem item, List<PersonInfo> people,
            LibraryOptions libraryOptions, CancellationToken cancellationToken) {
            if (!ShouldFetch(item, libraryOptions)) return ItemUpdateType.None;

            var pluginOptions = Plugin.Instance?.Options?.MetaData;
            if (pluginOptions == null) return ItemUpdateType.None;

            var enhanceCharacters = pluginOptions.EnableBangumiCharacters;
            var applyRating = pluginOptions.EnableBangumiRating;
            if (!enhanceCharacters && !applyRating) return ItemUpdateType.None;

            var budget = new BangumiRequestBudget(BangumiService.PerItemRequestBudget);
            var changed = false;

            try {
                var subjectId = await BangumiService.ResolveSubjectIdAsync(item, budget, cancellationToken)
                    .ConfigureAwait(false);
                if (subjectId <= 0) return ItemUpdateType.None;

                if (applyRating) {
                    var rating = await BangumiService.GetRatingAsync(subjectId, budget, cancellationToken)
                        .ConfigureAwait(false);
                    if (ApplyRating(item, rating)) changed = true;
                }

                if (enhanceCharacters) {
                    var result = await BangumiService
                        .EnhancePeopleRoleAsync(subjectId, people, budget, cancellationToken)
                        .ConfigureAwait(false);

                    if (result.Changed > 0) changed = true;

                    Plugin.SharedLogger?.Debug(
                        "BangumiProvider 角色增强完成: item={0}, subject={1}, matched={2}, changed={3}, 低置信={4}, 无中文名={5}, 未匹配={6}",
                        item.Name ?? string.Empty, subjectId, result.Matched, result.Changed, result.LowConfidence,
                        result.NoChineseName, result.Unmatched);
                }
            }
            catch (OperationCanceledException) {
                throw;
            }
            catch (Exception ex) {
                Plugin.SharedLogger?.Error("BangumiProvider 增强失败 item={0}", item.Name ?? string.Empty);
                Plugin.SharedLogger?.Error(ex.Message);
                Plugin.SharedLogger?.Debug(ex.StackTrace);
                return ItemUpdateType.None;
            }

            return changed ? ItemUpdateType.MetadataImport : ItemUpdateType.None;
        }

        private static bool ApplyRating(BaseItem item, BangumiStoredRating rating) {
            var pluginOptions = Plugin.Instance?.Options?.MetaData;
            if (pluginOptions == null || rating == null || rating.Score <= 0) return false;
            if (rating.Total < pluginOptions.BangumiRatingMinVotes) return false;
            if (!pluginOptions.EnableBangumiRatingOverride && item.CommunityRating.HasValue) return false;

            var score = (float)Math.Round(rating.Score, 1);
            if (item.CommunityRating.HasValue && Math.Abs(item.CommunityRating.Value - score) <= 0.05f) return false;

            item.CommunityRating = score;
            Plugin.SharedLogger?.Debug("BangumiProvider 评分已更新: item={0}, score={1}, total={2}",
                item.Name ?? string.Empty, score, rating.Total);
            return true;
        }

        private static bool ShouldFetch(BaseItem item, LibraryOptions libraryOptions) {
            return item != null &&
                   Plugin.Instance?.Options?.MainPage?.PlugginEnabled == true &&
                   libraryOptions != null &&
                   item.IsMetadataFetcherEnabled(libraryOptions, ProviderName);
        }
    }
}
