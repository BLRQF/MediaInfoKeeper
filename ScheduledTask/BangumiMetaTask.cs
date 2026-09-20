using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.ScheduledTask {
    /// <summary>
    /// 独立的 Bangumi 增强任务：不挂在 Emby 的元数据刷新上，手动触发，按本任务自己的媒体库范围处理。
    /// 单条目流程复用 BangumiRunner，与右键菜单同一份实现。
    /// </summary>
    public class BangumiMetaTask : IScheduledTask {
        private readonly ILogger logger;

        public BangumiMetaTask(ILogManager logManager) {
            logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Key => "MediaInfoKeeperBangumiMetaTask";

        public string Name => "08.Bangumi 角色与评分";

        public string Description =>
            "按本任务配置的媒体库范围，用 Bangumi 的中文角色名与评分增强剧集/电影，并合并进 .nfo。手动触发，不处理单集。";

        public string Category => Plugin.TaskCategoryName;

        // 纯手动触发：不提供默认触发器。
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            yield break;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress) {
            logger.Info("Bangumi 角色与评分计划任务开始");

            var root = Plugin.Instance?.Options;
            var options = root?.MetaData;
            if (options == null || root.MainPage?.PlugginEnabled != true) {
                progress?.Report(100.0);
                logger.Info("Bangumi 计划任务跳过：插件未启用");
                return;
            }

            if (!options.EnableBangumiCharacters && !options.EnableBangumiRating) {
                progress?.Report(100.0);
                logger.Info("Bangumi 计划任务跳过：角色增强与评分均未启用");
                return;
            }

            var scope = root.MainPage?.ScheduledTasksEditor?.BangumiMeta?.BangumiMetaLibraries;
            var libraryItems = Plugin.LibraryService?.FetchScheduledTaskLibraryItems(scope) ?? new List<BaseItem>();
            var targets = BangumiRunner.CollectTargets(libraryItems);

            if (targets.Count == 0) {
                progress?.Report(100.0);
                logger.Info("Bangumi 计划任务完成，条目数 0");
                return;
            }

            logger.Info("Bangumi 计划任务条目数 {0}，角色增强 {1}，评分 {2}，写 nfo {3}", targets.Count,
                options.EnableBangumiCharacters, options.EnableBangumiRating, options.EnableBangumiNfoWrite);

            var completed = 0;
            var nfoWritten = 0;
            var skipped = 0;

            foreach (var item in targets) {
                if (cancellationToken.IsCancellationRequested) {
                    logger.Info("Bangumi 计划任务已取消 item={0}", item.Name ?? string.Empty);
                    break;
                }

                try {
                    var outcome = await BangumiRunner.RunItemAsync(item, options, cancellationToken)
                        .ConfigureAwait(false);
                    if (!outcome.Processed) skipped++;
                    if (outcome.NfoWritten) nfoWritten++;
                }
                catch (OperationCanceledException) {
                    logger.Info("Bangumi 计划任务已取消 item={0}", item.Name ?? string.Empty);
                    break;
                }
                catch (Exception ex) {
                    logger.Error("Bangumi 计划任务处理失败 item={0}", item.Name ?? string.Empty);
                    logger.Error(ex.Message);
                    logger.Debug(ex.StackTrace);
                }

                progress?.Report(++completed / (double)targets.Count * 100);
            }

            BangumiStore.Flush();
            progress?.Report(100.0);
            logger.Info("Bangumi 计划任务完成：处理 {0} 个，写入 nfo {1} 个，跳过 {2} 个", completed, nfoWritten, skipped);
        }
    }
}
