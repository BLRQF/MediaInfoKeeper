using System.Threading.Tasks;

namespace MediaInfoKeeper.Options.View {
    internal sealed class TheIntroDbDialogView : MainPageTaskDialogView<MetaDataOptions.TheIntroDbEditorOptions> {
        private readonly MetaDataOptions owner;

        public TheIntroDbDialogView(string pluginId, MetaDataOptions owner)
            : base(pluginId,
                owner?.ScrapersEditor?.TheIntroDb ?? new MetaDataOptions.TheIntroDbEditorOptions(),
                "TheIntroDB") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScrapersEditor != null) owner.ScrapersEditor.TheIntroDb = Options;
        }
    }

    internal sealed class IntroDbDialogView : MainPageTaskDialogView<MetaDataOptions.IntroDbEditorOptions> {
        private readonly MetaDataOptions owner;

        public IntroDbDialogView(string pluginId, MetaDataOptions owner)
            : base(pluginId,
                owner?.ScrapersEditor?.IntroDb ?? new MetaDataOptions.IntroDbEditorOptions(),
                "IntroDB") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScrapersEditor != null) owner.ScrapersEditor.IntroDb = Options;
        }
    }

    internal sealed class BangumiDialogView : MainPageTaskDialogView<MetaDataOptions.BangumiEditorOptions> {
        private readonly MetaDataOptions owner;

        public BangumiDialogView(string pluginId, MetaDataOptions owner)
            : base(pluginId,
                owner?.ScrapersEditor?.Bangumi ?? new MetaDataOptions.BangumiEditorOptions(),
                "Bangumi") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScrapersEditor != null) owner.ScrapersEditor.Bangumi = Options;
        }
    }

    internal sealed class DanmuDialogView : MainPageTaskDialogView<MetaDataOptions.DanmuEditorOptions> {
        private readonly MetaDataOptions owner;

        public DanmuDialogView(string pluginId, MetaDataOptions owner)
            : base(pluginId,
                owner?.ScrapersEditor?.Danmu ?? new MetaDataOptions.DanmuEditorOptions(),
                "Danmu") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScrapersEditor != null) owner.ScrapersEditor.Danmu = Options;
        }
    }
}
