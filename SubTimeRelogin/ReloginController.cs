using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace SubTimeRelogin;

/// <summary>重连状态机的阶段。</summary>
internal enum ReloginStage
{
    Idle,
    WaitingForTitleMenu,
    OpeningCharacterSelect,
    SelectingCharacter,
}

/// <summary>
/// 掉线后的自动重连控制器。
///
/// 流程参考 DailyRoutines 的 AutoLogin / AutoIgnoreLoginLock，但去掉了对 OmenTools 的依赖：
///   1. 掉线（Logout code 90002）后启动重试计时；
///   2. 等到 _TitleMenu 就绪，向登录 Agent 发送事件 0/4「开始游戏」；
///   3. 等 _CharaSelectListMenu 就绪，先按服务器 ID 选服，再按 ContentId 选中角色；
///   4. 登录成功后重置状态。
/// 整个流程只依赖 AgentLobby 的 UI 事件与两个 Addon 的就绪判断。
/// </summary>
internal sealed class ReloginController : IDisposable
{
    private const string TitleMenuAddon = "_TitleMenu";
    private const string CharaSelectListMenuAddon = "_CharaSelectListMenu";
    private const string DialogueAddon = "Dialogue";

    private const int DisconnectCode = 90002;

    private readonly Configuration config;
    private readonly SubscriptionRecorder recorder;

    private ReloginStage stage = ReloginStage.Idle;
    private DateTime nextAttemptAt = DateTime.MinValue;
    private DateTime stageDeadline = DateTime.MinValue;
    private int attemptCount;

    public ReloginController(Configuration config, SubscriptionRecorder recorder)
    {
        this.config = config;
        this.recorder = recorder;

        Services.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, TitleMenuAddon, this.OnTitleMenu);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, CharaSelectListMenuAddon, this.OnCharacterSelect);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, DialogueAddon, this.OnDialogue);

        Services.ClientState.Logout += this.OnLogout;
        Services.ClientState.Login += this.OnLogin;
    }

    /// <summary>当前状态描述，供设置窗口显示。</summary>
    public string StatusDescription => this.stage switch
    {
        ReloginStage.Idle => "空闲",
        ReloginStage.WaitingForTitleMenu => $"等待标题界面（第 {this.attemptCount} 次尝试）",
        ReloginStage.OpeningCharacterSelect => $"打开角色选择（第 {this.attemptCount} 次尝试）",
        ReloginStage.SelectingCharacter => $"选择角色（第 {this.attemptCount} 次尝试）",
        _ => "空闲",
    };

    /// <summary>本月是否允许重连（月卡判定）。</summary>
    private bool CanRelogin()
    {
        if (!this.config.EnableAutoRelogin)
            return false;

        if (!this.config.RequireActiveMonthCard)
            return true;

        var record = this.config.GetBestRecord();
        if (record is null || record.RecordedAt == DateTime.MinValue)
            return this.config.ReloginWithoutSubscriptionData;

        // 月卡还有剩余的秒数，且按记录时间推算尚未到期。
        if (record.MonthCardSeconds <= 0)
            return false;

        var expiry = record.MonthCardExpiry;
        return expiry is not null && expiry.Value > DateTime.Now;
    }

    private void OnLogin()
    {
        this.Reset("登录成功");
    }

    private void OnLogout(int type, int code)
    {
        // type == 2 表示客户端会弹出 Dialogue，code == 90002 表示与服务器断开。
        var isDisconnect = code == DisconnectCode || type == 2;
        if (!isDisconnect)
        {
            Services.Log.Debug($"[SubTimeRelogin] 主动登出（type={type}, code={code}），不重连");
            return;
        }

        if (!this.CanRelogin())
        {
            Services.Log.Information("[SubTimeRelogin] 掉线，但月卡已无剩余（或未开启自动重连），跳过重连");
            return;
        }

        this.attemptCount = 0;
        this.stage = ReloginStage.WaitingForTitleMenu;
        this.nextAttemptAt = DateTime.Now.AddSeconds(Math.Max(1, this.config.RetryIntervalSeconds));
        this.stageDeadline = DateTime.MaxValue;
        Services.Log.Information($"[SubTimeRelogin] 检测到掉线，准备自动重连（{this.config.RetryIntervalSeconds} 秒后开始）");
    }

    private void Reset(string reason)
    {
        if (this.stage != ReloginStage.Idle)
            Services.Log.Debug($"[SubTimeRelogin] 重置重连状态: {reason}");

        this.stage = ReloginStage.Idle;
        this.attemptCount = 0;
        this.nextAttemptAt = DateTime.MinValue;
        this.stageDeadline = DateTime.MaxValue;
    }

    /// <summary>由 Framework.Update 驱动，低频调用。</summary>
    public void Update()
    {
        if (this.stage == ReloginStage.Idle)
            return;

        var now = DateTime.Now;

        // 已经回到游戏内，说明重连成功。
        if (Services.ClientState.IsLoggedIn)
        {
            this.Reset("已重新登录");
            return;
        }

        if (now < this.nextAttemptAt)
            return;

        // 掉线提示框会挡在标题界面前面，先把它关掉再继续。
        if (this.TryDismissDialogue())
            return;

        if (this.config.MaxReloginAttempts > 0 && this.attemptCount >= this.config.MaxReloginAttempts)
        {
            Services.ChatGui.Print("[SubTimeRelogin] 已达到最大重试次数，停止自动重连。");
            this.Reset("达到最大重试次数");
            return;
        }

        // 每一步都有超时保护，避免卡死在某个界面。
        if (now > this.stageDeadline)
        {
            Services.Log.Debug($"[SubTimeRelogin] 阶段 {this.stage} 超时，重试");
            this.stage = ReloginStage.WaitingForTitleMenu;
            this.nextAttemptAt = now.AddSeconds(Math.Max(1, this.config.RetryIntervalSeconds));
            this.stageDeadline = DateTime.MaxValue;
            return;
        }

        switch (this.stage)
        {
            case ReloginStage.WaitingForTitleMenu:
                this.TickWaitingForTitle(now);
                break;
            case ReloginStage.OpeningCharacterSelect:
                this.TickOpeningCharacterSelect(now);
                break;
            case ReloginStage.SelectingCharacter:
                this.TickSelectingCharacter(now);
                break;
        }
    }

    private void TickWaitingForTitle(DateTime now)
    {
        if (SubscriptionReader.IsOnCharacterSelect())
        {
            // 已经在角色选择界面，直接选角色。
            this.stage = ReloginStage.SelectingCharacter;
            this.nextAttemptAt = now;
            this.EnterStage(now);
            return;
        }

        if (!AgentHelper.IsAddonReady(TitleMenuAddon))
            return;

        this.attemptCount++;
        this.stage = ReloginStage.OpeningCharacterSelect;
        this.EnterStage(now);
    }

    private void TickOpeningCharacterSelect(DateTime now)
    {
        if (SubscriptionReader.IsOnCharacterSelect())
        {
            this.stage = ReloginStage.SelectingCharacter;
            this.EnterStage(now);
            return;
        }

        if (!AgentHelper.IsAddonReady(TitleMenuAddon))
            return;

        if (this.config.IgnoreLoginLock)
            AgentHelper.ClearTemporaryLock();

        AgentHelper.OpenCharacterSelect();
        this.stage = ReloginStage.SelectingCharacter;
        this.EnterStage(now);
    }

    private void TickSelectingCharacter(DateTime now)
    {
        if (!SubscriptionReader.IsOnCharacterSelect())
            return;

        if (!AgentHelper.IsAddonReady(CharaSelectListMenuAddon))
            return;

        if (this.config.IgnoreLoginLock)
            AgentHelper.ClearTemporaryLock();

        var worldId = this.config.LastWorldId;
        if (worldId != 0)
        {
            var worldIndex = SubscriptionReader.FindWorldIndexById(worldId);
            if (worldIndex >= 0 && worldIndex != SubscriptionReader.GetWorldIndex())
            {
                AgentHelper.SelectWorldByIndex(worldIndex);
                this.stageDeadline = now.AddSeconds(15);
                return;
            }
        }

        var characterIndex = SubscriptionReader.FindCharacterIndex(this.config.LastContentId);
        if (characterIndex < 0)
        {
            // 记录的角色不在当前列表里，退而求其次选第一个。
            characterIndex = 0;
        }

        AgentHelper.SelectCharacterByIndex(characterIndex);
        Services.Log.Information($"[SubTimeRelogin] 第 {this.attemptCount} 次尝试登录角色（下标 {characterIndex}）");

        this.stage = ReloginStage.WaitingForTitleMenu;
        this.nextAttemptAt = now.AddSeconds(Math.Max(1, this.config.RetryIntervalSeconds));
        this.stageDeadline = DateTime.MaxValue;
    }

    private void EnterStage(DateTime now)
    {
        this.stageDeadline = now.AddSeconds(20);
        this.nextAttemptAt = now.AddMilliseconds(250);
    }

    /// <summary>
    /// 若断线 / 登录失败提示框正开着，点掉它的确认按钮。
    /// 返回是否点掉了（调用方据此让本轮更新提前结束）。
    /// </summary>
    private unsafe bool TryDismissDialogue()
    {
        if (!this.config.AutoAcceptDialogue)
            return false;

        var addon = AgentHelper.GetAddon(DialogueAddon);
        if (!AgentHelper.IsAddonReady(addon))
            return false;

        AgentHelper.ClickButton(addon, 4);
        return true;
    }

    private void OnTitleMenu(AddonEvent type, AddonArgs args)
    {
        if (this.stage != ReloginStage.WaitingForTitleMenu)
            return;

        // 标题界面刚出现，稍等一拍再操作，避免 Addon 尚未就绪。
        this.nextAttemptAt = DateTime.Now;
    }

    private void OnCharacterSelect(AddonEvent type, AddonArgs args)
    {
        // 重连途中路过角色选择界面时顺手刷新一次订阅记录。
        this.recorder.Capture(0);

        if (this.stage == ReloginStage.WaitingForTitleMenu || this.stage == ReloginStage.OpeningCharacterSelect)
        {
            this.stage = ReloginStage.SelectingCharacter;
            this.nextAttemptAt = DateTime.Now.AddMilliseconds(500);
            this.stageDeadline = DateTime.Now.AddSeconds(20);
        }
    }

    /// <summary>
    /// 自动点掉 Dialogue 上的确认按钮。登录失败、排队、临时锁都会弹这个框，
    /// 不点掉它客户端就不会继续尝试。
    /// </summary>
    private void OnDialogue(AddonEvent type, AddonArgs args)
    {
        if (this.stage == ReloginStage.Idle)
            return;

        if (!this.TryDismissDialogue())
            return;

        // 关掉对话框后让流程重新从标题界面开始。
        this.stage = ReloginStage.WaitingForTitleMenu;
        this.nextAttemptAt = DateTime.Now.AddSeconds(Math.Max(1, this.config.RetryIntervalSeconds));
        this.stageDeadline = DateTime.MaxValue;
    }

    public void Dispose()
    {
        Services.ClientState.Logout -= this.OnLogout;
        Services.ClientState.Login -= this.OnLogin;

        Services.AddonLifecycle.UnregisterListener(this.OnTitleMenu);
        Services.AddonLifecycle.UnregisterListener(this.OnCharacterSelect);
        Services.AddonLifecycle.UnregisterListener(this.OnDialogue);
    }
}
