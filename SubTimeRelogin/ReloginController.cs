using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace SubTimeRelogin;

/// <summary>重连状态机的阶段。</summary>
internal enum ReloginStage
{
    Idle,

    /// <summary>等 _TitleMenu 就绪，然后发送「开始游戏」事件进入角色选择。</summary>
    OpenCharacterSelect,

    /// <summary>在角色选择界面切换到目标服务器，并等待切换真正生效。</summary>
    ResolveWorld,

    /// <summary>服务器到位后，按 ContentId 选中角色。</summary>
    SelectCharacter,
}

/// <summary>
/// 掉线后的自动重连控制器。
///
/// 登录流程刻意对齐 DailyRoutines 的 AutoLogin 原版（三步 EnqueueLogin）：
///   1. _TitleMenu 就绪 → 向登录 Agent 发送事件 0/4「开始游戏」；
///   2. 等 _CharaSelectListMenu 就绪 → 选服，**并等待 agent->WorldId 变成目标服务器**；
///   3. 服务器确认到位后，才在角色列表里按 ContentId 找到角色下标并选中（事件 0/21 + 3/0）。
///
/// 第 2 步的「等待服务器真正切过去」是原版的关键，之前省略它会立刻按旧列表的
/// 下标选角色，于是选到别的区里下标相同的角色 —— 也就是「选错区」。
/// </summary>
internal sealed class ReloginController : IDisposable
{
    private const string TitleMenuAddon = "_TitleMenu";
    private const string CharaSelectListMenuAddon = "_CharaSelectListMenu";
    private const string DialogueAddon = "Dialogue";

    private const int DisconnectCode = 90002;

    /// <summary>单个阶段的超时（秒），超时后回到「打开角色选择」重新开始。</summary>
    private const int StageTimeoutSeconds = 20;

    private readonly Configuration config;
    private readonly SubscriptionRecorder recorder;

    private ReloginStage stage = ReloginStage.Idle;
    private DateTime nextAttemptAt = DateTime.MinValue;
    private DateTime stageDeadline = DateTime.MaxValue;
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
        ReloginStage.OpenCharacterSelect => $"打开角色选择（第 {this.attemptCount} 次尝试）",
        ReloginStage.ResolveWorld => $"切换服务器（第 {this.attemptCount} 次尝试）",
        ReloginStage.SelectCharacter => $"选择角色（第 {this.attemptCount} 次尝试）",
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
        this.stage = ReloginStage.OpenCharacterSelect;
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

        if (now > this.stageDeadline)
        {
            Services.Log.Debug($"[SubTimeRelogin] 阶段 {this.stage} 超时，重新开始");
            this.stage = ReloginStage.OpenCharacterSelect;
            this.nextAttemptAt = now.AddSeconds(Math.Max(1, this.config.RetryIntervalSeconds));
            this.stageDeadline = DateTime.MaxValue;
            return;
        }

        switch (this.stage)
        {
            case ReloginStage.OpenCharacterSelect:
                this.TickOpenCharacterSelect(now);
                break;
            case ReloginStage.ResolveWorld:
                this.TickResolveWorld(now);
                break;
            case ReloginStage.SelectCharacter:
                this.TickSelectCharacter(now);
                break;
        }
    }

    /// <summary>原版步骤 1：点「开始游戏」，进入角色选择界面。</summary>
    private void TickOpenCharacterSelect(DateTime now)
    {
        // 已经站在角色选择界面上，直接进入选服阶段。
        if (SubscriptionReader.IsOnCharacterSelect() && AgentHelper.IsAddonReady(CharaSelectListMenuAddon))
        {
            this.RecordSubscriptionOnFirstLogin();
            this.stage = ReloginStage.ResolveWorld;
            this.EnterStage(now);
            return;
        }

        if (!AgentHelper.IsAddonReady(TitleMenuAddon))
            return;

        if (this.config.IgnoreLoginLock)
            AgentHelper.ClearTemporaryLock();

        this.attemptCount++;
        AgentHelper.OpenCharacterSelect();

        this.EnterStage(now);
    }

    /// <summary>原版步骤 2：选服，并等待服务器真正切换到位。</summary>
    private void TickResolveWorld(DateTime now)
    {
        if (!SubscriptionReader.IsOnCharacterSelect())
        {
            this.stage = ReloginStage.OpenCharacterSelect;
            this.EnterStage(now);
            return;
        }

        if (!AgentHelper.IsAddonReady(CharaSelectListMenuAddon))
            return;

        if (this.config.IgnoreLoginLock)
            AgentHelper.ClearTemporaryLock();

        this.RecordSubscriptionOnFirstLogin();

        var targetWorld = this.ResolveTargetWorld();
        if (targetWorld == 0)
        {
            // 没有可用的目标服信息，直接在当前服务器选角色。
            this.stage = ReloginStage.SelectCharacter;
            this.EnterStage(now);
            return;
        }

        if (SubscriptionReader.GetSelectedWorldId() == targetWorld)
        {
            this.stage = ReloginStage.SelectCharacter;
            this.EnterStage(now);
            return;
        }

        var worldIndex = SubscriptionReader.FindWorldIndexById(targetWorld);
        if (worldIndex < 0)
        {
            Services.Log.Warning($"[SubTimeRelogin] 当前大区服务器列表里没有世界 {targetWorld}，改为在当前服务器选角色");
            this.stage = ReloginStage.SelectCharacter;
            this.EnterStage(now);
            return;
        }

        AgentHelper.SelectWorldByIndex(worldIndex);
        Services.Log.Debug($"[SubTimeRelogin] 切换到世界 {targetWorld}（下标 {worldIndex}），等待生效");
        this.EnterStage(now);
    }

    /// <summary>原版步骤 3：确认服务器到位后，按 ContentId 选角色。</summary>
    private void TickSelectCharacter(DateTime now)
    {
        if (!SubscriptionReader.IsOnCharacterSelect())
        {
            this.stage = ReloginStage.OpenCharacterSelect;
            this.EnterStage(now);
            return;
        }

        if (!AgentHelper.IsAddonReady(CharaSelectListMenuAddon))
            return;

        // 服务器还没切到位时不要选角色，否则会按旧列表的下标选到别的区。
        var targetWorld = this.ResolveTargetWorld();
        if (targetWorld != 0 && SubscriptionReader.GetSelectedWorldId() != targetWorld)
        {
            this.stage = ReloginStage.ResolveWorld;
            this.EnterStage(now);
            return;
        }

        var characterIndex = SubscriptionReader.FindCharacterIndex(this.config.LastContentId);
        if (characterIndex < 0)
        {
            Services.Log.Warning($"[SubTimeRelogin] 角色列表里找不到 ContentId {this.config.LastContentId}，改为选择第一个角色");
            characterIndex = 0;
        }

        AgentHelper.SelectCharacterByIndex(characterIndex);
        Services.Log.Information($"[SubTimeRelogin] 第 {this.attemptCount} 次尝试登录角色（下标 {characterIndex}）");

        this.stage = ReloginStage.OpenCharacterSelect;
        this.nextAttemptAt = now.AddSeconds(Math.Max(1, this.config.RetryIntervalSeconds));
        this.stageDeadline = DateTime.MaxValue;
    }

    /// <summary>
    /// 计算本次登录要切到哪个服务器：
    /// 优先用「最近登录角色的 ContentId」在登录界面的大区角色列表里查，
    /// 跨服传送中用当前所在服，否则用归属服（与 AutoLogin 一致）；
    /// 查不到时退回上次记录的服务器 ID。
    /// </summary>
    private uint ResolveTargetWorld()
    {
        if (this.config.LastContentId != 0
            && SubscriptionReader.TryGetLoginTarget(this.config.LastContentId, out var world))
            return world;

        return this.config.LastWorldId;
    }

    /// <summary>
    /// 第一次登录到角色选择界面时读取一次订阅时间。
    /// 订阅信息只在登录握手后由服务器下发，重复读取不会得到新值，
    /// 所以这里只在本次会话的第一次进入角色选择时抓取。
    /// </summary>
    private void RecordSubscriptionOnFirstLogin() => this.recorder.CaptureOnFirstLogin();

    private void EnterStage(DateTime now)
    {
        this.stageDeadline = now.AddSeconds(StageTimeoutSeconds);
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
        if (this.stage != ReloginStage.OpenCharacterSelect)
            return;

        // 标题界面刚出现，稍等一拍再操作，避免 Addon 尚未就绪。
        this.nextAttemptAt = DateTime.Now;
    }

    private void OnCharacterSelect(AddonEvent type, AddonArgs args)
    {
        // 重连途中路过角色选择界面时抓一次订阅时间（只在第一次生效）。
        this.RecordSubscriptionOnFirstLogin();

        if (this.stage == ReloginStage.OpenCharacterSelect)
        {
            this.stage = ReloginStage.ResolveWorld;
            this.nextAttemptAt = DateTime.Now.AddMilliseconds(500);
            this.stageDeadline = DateTime.Now.AddSeconds(StageTimeoutSeconds);
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

        // 关掉对话框后让流程重新从「打开角色选择」开始。
        this.stage = ReloginStage.OpenCharacterSelect;
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
