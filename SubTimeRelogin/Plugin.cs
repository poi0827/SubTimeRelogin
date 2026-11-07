using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SubTimeRelogin;

/// <summary>
/// 插件入口：记录国服点卡 / 月卡剩余时间，并在月卡仍有剩余时于掉线后自动重连。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/sublogin";

    private readonly WindowSystem windowSystem = new("SubTimeRelogin");
    private readonly Configuration config;
    private readonly SubscriptionRecorder recorder;
    private readonly ReloginController reloginController;
    private readonly ConfigWindow configWindow;

    private DateTime lastUpdateAt = DateTime.MinValue;
    private bool disposed;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IClientState clientState,
        IPlayerState playerState,
        IAddonLifecycle addonLifecycle,
        IFramework framework,
        IDtrBar dtrBar,
        ICommandManager commandManager,
        IChatGui chatGui,
        IGameGui gameGui,
        IDataManager dataManager,
        ICondition condition,
        IPluginLog log)
    {
        Services.Initialize(
            pluginInterface,
            clientState,
            playerState,
            addonLifecycle,
            framework,
            dtrBar,
            commandManager,
            chatGui,
            gameGui,
            dataManager,
            condition,
            log);

        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.config.Initialize(pluginInterface);

        this.recorder = new SubscriptionRecorder(this.config, this.OpenConfigUi);
        this.reloginController = new ReloginController(this.config, this.recorder);

        this.configWindow = new ConfigWindow(this.config, this.BuildStatusText, this.OnConfigChanged);
        this.windowSystem.AddWindow(this.configWindow);

        pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += this.OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += this.OpenMainUi;

        Services.ClientState.Login += this.OnLogin;
        Services.Framework.Update += this.OnFrameworkUpdate;
        Services.CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "打开 SubTimeRelogin 设置窗口",
        });

        Services.Log.Information("[SubTimeRelogin] 插件已加载");
    }

    private void OnLogin()
    {
        this.recorder.OnLogin();

        // 登录后再补一次读取：此时订阅信息可能刚刚被服务器填充。
        Services.Framework.RunOnTick(() => this.recorder.Capture(0), TimeSpan.FromSeconds(5));
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.Now;
        if ((now - this.lastUpdateAt).TotalSeconds < 1)
            return;

        this.lastUpdateAt = now;

        try
        {
            this.recorder.Update();
            this.reloginController.Update();
        }
        catch (Exception ex)
        {
            Services.Log.Error($"[SubTimeRelogin] 周期任务异常: {ex}");
        }
    }

    private void OnCommand(string command, string arguments)
    {
        var args = arguments.Trim();
        if (args.Equals("record", StringComparison.OrdinalIgnoreCase))
        {
            var captured = this.recorder.Capture(0);
            Services.ChatGui.Print(captured
                                       ? $"[SubTimeRelogin] 已记录订阅时间，月卡剩余 {this.recorder.Current?.MonthCardRemaining}。"
                                       : "[SubTimeRelogin] 读取订阅信息失败，请先进入角色选择界面。");
            return;
        }

        this.OpenConfigUi();
    }

    private void OpenConfigUi() => this.configWindow.IsOpen = true;

    /// <summary>
    /// 主界面入口与配置界面是同一个窗口：本插件只有一个设置 / 状态面板。
    /// </summary>
    private void OpenMainUi() => this.configWindow.IsOpen = true;

    private void OnConfigChanged() => this.config.Save();

    private string BuildStatusText()
    {
        var record = this.config.GetBestRecord();
        if (record is null || record.RecordedAt == DateTime.MinValue)
            return "尚未记录到订阅时间。请在角色选择界面停留片刻，或使用 /sublogin record 手动读取。";

        var lines = new[]
        {
            $"上次记录：{record.RecordedAt:yyyy-MM-dd HH:mm:ss}",
            $"月卡剩余：{record.MonthCardRemaining?.ToString() ?? "无"}",
            $"点卡剩余：{record.PointCardRemaining?.ToString() ?? "无"}",
            $"重连状态：{this.reloginController.StatusDescription}",
            $"记录角色数：{this.config.Records.Count}",
        };

        return string.Join("\n", lines);
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        Services.CommandManager.RemoveHandler(CommandName);
        Services.Framework.Update -= this.OnFrameworkUpdate;
        Services.ClientState.Login -= this.OnLogin;

        Services.PluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= this.OpenConfigUi;
        Services.PluginInterface.UiBuilder.OpenMainUi -= this.OpenMainUi;

        this.windowSystem.RemoveAllWindows();
        this.reloginController.Dispose();
        this.recorder.Dispose();
        this.config.Save();
    }
}
