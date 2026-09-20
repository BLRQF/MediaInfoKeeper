using System;
using System.Threading.Tasks;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaInfoKeeper.Options.Store;
using MediaInfoKeeper.Options.UIBaseClasses.Views;

namespace MediaInfoKeeper.Options.View {
    internal class MetaDataPageView : PluginPageView {
        private const string TheIntroDbDialogCommandId = "metadata.scraper.theIntroDb";
        private const string IntroDbDialogCommandId = "metadata.scraper.introDb";
        private const string DanmuDialogCommandId = "metadata.scraper.danmu";
        private const string BangumiDialogCommandId = "metadata.scraper.bangumi";
        private readonly MetaDataOptionsStore store;

        public MetaDataPageView(PluginInfo pluginInfo, MetaDataOptionsStore store)
            : base(pluginInfo.Id) {
            this.store = store;
            ContentData = store.GetOptions();
        }

        public MetaDataOptions Options => ContentData as MetaDataOptions;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data) {
            if (string.Equals(commandId, TheIntroDbDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(new TheIntroDbDialogView(PluginId, Options));

            if (string.Equals(commandId, IntroDbDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(new IntroDbDialogView(PluginId, Options));

            if (string.Equals(commandId, DanmuDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(new DanmuDialogView(PluginId, Options));

            if (string.Equals(commandId, BangumiDialogCommandId, StringComparison.Ordinal))
                return Task.FromResult<IPluginUIView>(new BangumiDialogView(PluginId, Options));

            return base.RunCommand(itemId, commandId, data);
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data) {
            store.SetOptions(Options);
            return base.OnSaveCommand(itemId, commandId, data);
        }

        public override void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data) {
            base.OnDialogResult(dialogView, completedOk, data);
            if (completedOk) store.SetOptions(Options);
        }
    }
}
