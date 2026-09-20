using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaInfoKeeper.Options;
using MediaInfoKeeper.Services;

namespace MediaInfoKeeper.Web.Handler {
    internal sealed class EnhanceBangumiRouteHandler {
        private readonly Func<IEnumerable<string>, List<BaseItem>> _expandToTargetItems;

        public EnhanceBangumiRouteHandler(Func<IEnumerable<string>, List<BaseItem>> expandToTargetItems) {
            _expandToTargetItems = expandToTargetItems;
        }

        public MediaInfoMenuResponse Handle(EnhanceBangumiRequest request) {
            var response = new MediaInfoMenuResponse();

            if (request?.Ids == null || request.Ids.Length == 0) {
                response.Message = "no items";
                return response;
            }

            var options = Plugin.Instance.Options.MetaData;
            if (Plugin.Instance.Options.MainPage?.PlugginEnabled != true) {
                response.Total = request.Ids.Length;
                response.Skipped = request.Ids.Length;
                response.Message = "plugin disabled";
                return response;
            }

            if (!options.EnableBangumiCharacters && !options.EnableBangumiRating) {
                response.Total = request.Ids.Length;
                response.Skipped = request.Ids.Length;
                response.Message = "bangumi disabled";
                return response;
            }

            // ExpandItem 会把 Series / Season 展开成分集，本功能工作在剧集/电影层级，所以再归并回去。
            var expanded = _expandToTargetItems(request.Ids);
            var targets = BangumiRunner.CollectTargets(expanded);
            response.Total = targets.Count;

            if (targets.Count == 0) {
                response.Message = "no supported items";
                return response;
            }

            foreach (var item in targets) {
                response.Processed++;
                try {
                    FireAndForgetEnhanceSingleItem(item, options);
                    response.Succeeded++;
                }
                catch (Exception ex) {
                    response.Failed++;
                    LogFailure("提交", item, ex);
                }
            }

            response.Message = "submitted";
            Plugin.Instance.Logger.Info(
                $"ShortcutMenu EnhanceBangumi result: total={response.Total}, processed={response.Processed}, succeeded={response.Succeeded}, failed={response.Failed}, skipped={response.Skipped}, message={response.Message}");
            return response;
        }

        private static void FireAndForgetEnhanceSingleItem(BaseItem item, MetaDataOptions options) {
            _ = Task.Run(async () => {
                try {
                    var outcome = await BangumiRunner.RunItemAsync(item, options, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (!outcome.Processed)
                        Plugin.Instance.Logger.Info($"快捷菜单 Bangumi 增强未解析到条目: {item.Path ?? item.Name}");
                }
                catch (Exception ex) {
                    LogFailure("增强", item, ex);
                }
                finally {
                    BangumiStore.Flush();
                }
            });
        }

        private static void LogFailure(string stage, BaseItem item, Exception ex) {
            Plugin.Instance.Logger.Error($"快捷菜单 Bangumi {stage}失败: {item?.Path ?? item?.Name}");
            Plugin.Instance.Logger.Error(ex.Message);
            Plugin.Instance.Logger.Debug(ex.StackTrace);
        }
    }
}
