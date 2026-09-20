using System.Threading.Tasks;

namespace MediaInfoKeeper.Options.View {
    internal sealed class
        UpdatePluginTaskDialogView : MainPageTaskDialogView<MainPageOptions.UpdatePluginTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public UpdatePluginTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.UpdatePlugin ?? new MainPageOptions.UpdatePluginTaskEditorOptions(),
                "更新插件") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.UpdatePlugin = Options;
        }
    }

    internal sealed class
        RefreshRecentMetadataTaskDialogView : MainPageTaskDialogView<
        MainPageOptions.RefreshRecentMetadataTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public RefreshRecentMetadataTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.RefreshRecentMetadata ??
                new MainPageOptions.RefreshRecentMetadataTaskEditorOptions(), "刷新媒体元数据") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.RefreshRecentMetadata = Options;
        }
    }

    internal sealed class
        SubmitTheIntroDbMarkersTaskDialogView : MainPageTaskDialogView<
        MainPageOptions.SubmitTheIntroDbMarkersTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public SubmitTheIntroDbMarkersTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.SubmitTheIntroDbMarkers ??
                new MainPageOptions.SubmitTheIntroDbMarkersTaskEditorOptions(), "共享片头片尾") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.SubmitTheIntroDbMarkers = Options;
        }
    }

    internal sealed class
        ExportExistingMediaInfoTaskDialogView : MainPageTaskDialogView<
        MainPageOptions.ExportExistingMediaInfoTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public ExportExistingMediaInfoTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.ExportExistingMediaInfo ??
                new MainPageOptions.ExportExistingMediaInfoTaskEditorOptions(), "备份媒体信息") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.ExportExistingMediaInfo = Options;
        }
    }

    internal sealed class
        RestoreMediaInfoTaskDialogView : MainPageTaskDialogView<MainPageOptions.RestoreMediaInfoTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public RestoreMediaInfoTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.RestoreMediaInfo ??
                new MainPageOptions.RestoreMediaInfoTaskEditorOptions(), "恢复媒体信息") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.RestoreMediaInfo = Options;
        }
    }

    internal sealed class
        BangumiMetaTaskDialogView : MainPageTaskDialogView<MainPageOptions.BangumiMetaTaskEditorOptions> {
        private readonly MainPageOptions owner;

        public BangumiMetaTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId,
                owner?.ScheduledTasksEditor?.BangumiMeta ?? new MainPageOptions.BangumiMetaTaskEditorOptions(),
                "Bangumi 角色与评分") {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data) {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.BangumiMeta = Options;
        }
    }
}
