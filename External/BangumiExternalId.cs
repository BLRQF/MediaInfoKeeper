using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace MediaInfoKeeper.External {
    public class BangumiExternalId : IExternalId {
        private string subjectId;

        public static string StaticName => "Bangumi";

        public string Name => "Bangumi";

        public string Key => StaticName;

        public string UrlFormatString => string.IsNullOrWhiteSpace(subjectId)
            ? "https://bgm.tv/subject/{0}"
            : $"https://bgm.tv/subject/{subjectId}";

        public bool Supports(IHasProviderIds item) {
            subjectId = item?.GetProviderId(StaticName)?.Trim();
            return item is Movie || item is Series;
        }
    }
}
