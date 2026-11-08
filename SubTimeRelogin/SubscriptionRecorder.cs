using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SubTimeRelogin;

/// <summary>
/// 负责读取、记录订阅剩余时间，并把它显示到服务器信息栏（DTR）与角色选择界面。
/// </summary>
internal sealed class SubscriptionRecorder : IDisposable
{
    private const string DtrTitle = "SubTimeRelogin-Subscription";
    private const string CharaSelectAddon = "_CharaSelectRemain";
    private const string CharaSelectAddon2 = "CharaSelect";

    /// <summary>国服时长充值页面（与原版 AutoRecordSubTimeLeft 右键一致）。</summary>
    private const string RechargeUrl = "https://pay.sdo.com/item/GWPAY-100001900";

    private readonly Configuration config;
    private readonly IDtrBarEntry? dtrEntry;
    private readonly Action openConfig;
    private bool disposed;

    public SubscriptionRecorder(Configuration config, Action openConfig)
    {
        this.config = config;
        this.openConfig = openConfig;

        // 条目始终创建：只在显示时受 ShowDtrEntry 控制，这样在设置里开关能立即生效。
        try
        {
            this.dtrEntry = Services.DtrBar.Get(DtrTitle, "订阅时间");
            if (this.dtrEntry != null)
            {
                this.dtrEntry.OnClick = this.OnDtrClick;
                this.dtrEntry.Shown = false;
            }
        }
        catch (Exception ex)
        {
            Services.Log.Warning($"[SubTimeRelogin] 创建服务器信息栏条目失败: {ex.Message}");
        }

        Services.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, CharaSelectAddon, this.OnAddonUpdate);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, CharaSelectAddon2, this.OnAddonUpdate);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, CharaSelectAddon, this.OnAddonUpdate);
    }

    /// <summary>当前记录，供重连判定使用。</summary>
    public SubscriptionRecord? Current => this.config.GetBestRecord();

    /// <summary>月卡是否仍有剩余时间。</summary>
    public bool HasActiveMonthCard
    {
        get
        {
            var record = this.config.GetBestRecord();
            if (record is null || record.RecordedAt == DateTime.MinValue)
                return false;

            if (record.MonthCardSeconds <= 0)
                return false;

            var remaining = record.MonthCardExpiry - DateTime.Now;
            return remaining > TimeSpan.Zero;
        }
    }

    /// <summary>
    /// 读取一次订阅信息并写入配置，返回是否读到了有效数据。
    /// </summary>
    public bool Capture(ulong contentId)
    {
        if (!SubscriptionReader.TryRead(out var monthSeconds, out var pointSeconds))
            return false;

        if (contentId == 0)
            contentId = Services.PlayerState.ContentId;

        var record = new SubscriptionRecord
        {
            RecordedAt = DateTime.Now,
            MonthCardSeconds = monthSeconds,
            PointCardSeconds = pointSeconds,
        };

        if (contentId != 0)
        {
            this.config.Records.TryGetValue(contentId, out var existing);
            if (SubscriptionReader.IsUnchanged(existing, monthSeconds, pointSeconds))
                return true;

            this.config.Records[contentId] = record;
        }

        this.config.AccountRecord = record;
        this.config.Save();
        this.UpdateDtr();
        return true;
    }

    /// <summary>登录成功后调用：记录当前角色信息并抓取订阅时间。</summary>
    public void OnLogin()
    {
        try
        {
            this.config.LastContentId = Services.PlayerState.ContentId;
            this.config.LastWorldId = Services.PlayerState.CurrentWorld.RowId;
            this.config.LastCharacterName = Services.PlayerState.CharacterName;
            this.config.Save();
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"[SubTimeRelogin] 记录登录角色信息失败: {ex.Message}");
        }

        this.UpdateDtr();
    }

    /// <summary>周期性调用（由 Framework.Update 驱动，低频）。</summary>
    public void Update()
    {
        if (this.config.ShowRemainingOnCharacterSelect)
            this.UpdateCharacterSelectRemain();

        this.UpdateDtr();
    }

    private void OnDtrClick(DtrInteractionEvent evt)
    {
        // 与原版一致：左键打开配置界面，右键打开国服时长充值页面。
        if (evt.ClickType == MouseClickType.Right)
        {
            try
            {
                Util.OpenLink(RechargeUrl);
            }
            catch (Exception ex)
            {
                Services.Log.Debug($"[SubTimeRelogin] 打开充值页面失败: {ex.Message}");
            }

            return;
        }

        this.openConfig();
    }

    private unsafe void OnAddonUpdate(AddonEvent type, AddonArgs args)
    {
        if (!this.config.ShowRemainingOnCharacterSelect)
            return;

        this.UpdateCharacterSelectRemain();

        // 停留在角色选择界面时，顺便刷新一次记录。
        var contentId = SubscriptionReader.GetHoveredCharacterContentId();
        if (contentId != 0)
            this.Capture(contentId);
    }

    private unsafe void UpdateCharacterSelectRemain()
    {
        if (!this.config.ShowRemainingOnCharacterSelect)
            return;

        if (!SubscriptionReader.IsOnCharacterSelect())
            return;

        var contentId = SubscriptionReader.GetHoveredCharacterContentId();
        SubscriptionRecord? record = null;
        if (contentId != 0)
            this.config.Records.TryGetValue(contentId, out record);

        record ??= this.config.GetBestRecord();

        if (record is null || record.RecordedAt == DateTime.MinValue)
            return;

        var addon = AgentHelper.GetAddon(CharaSelectAddon);
        if (!AgentHelper.IsAddonReady(addon))
            return;

        var textNode = addon->GetTextNodeById(7);
        if (textNode == null)
            return;

        var builder = new StringBuilder();
        builder.Append("月卡：").Append(FormatRemaining(record.MonthCardRemaining));
        builder.Append('\n');
        builder.Append("点卡：").Append(FormatRemaining(record.PointCardRemaining));

        textNode->SetPositionFloat(-20, 40);
        textNode->SetText(builder.ToString());
    }

    private void UpdateDtr()
    {
        if (this.dtrEntry is null)
            return;

        try
        {
            if (!this.config.ShowDtrEntry)
            {
                this.dtrEntry.Shown = false;
                return;
            }

            var record = this.config.GetBestRecord();
            if (record is null || record.RecordedAt == DateTime.MinValue)
            {
                this.dtrEntry.Shown = false;
                return;
            }

            // 与原版一致：战斗中隐藏，避免刷屏干扰。
            if (Services.Condition[ConditionFlag.InCombat])
            {
                this.dtrEntry.Shown = false;
                return;
            }

            // 月卡与点卡都没有剩余时没有可显示的到期时间。
            var expiry = record.MonthCardExpiry ?? record.PointCardExpiry;
            if (expiry is null)
            {
                this.dtrEntry.Shown = false;
                return;
            }

            // 原版：有月卡就显示月卡到期时间，否则显示点卡到期时间。
            var isMonth = record.MonthCardExpiry is not null && record.MonthCardSeconds > 0;
            var label = isMonth ? "月卡" : "点卡";

            this.dtrEntry.Text = new SeStringBuilder()
                                .AddUiForeground($"[{label}] ", 25)
                                .AddText($"{expiry.Value:MM/dd HH:mm}")
                                .Build();

            this.dtrEntry.Tooltip = new SeStringBuilder()
                                   .AddUiForeground("[过期时间]", 28)
                                   .AddText($"\n{expiry.Value:yyyy/MM/dd HH:mm:ss}")
                                   .AddText("\n")
                                   .AddUiForeground("[剩余时长]", 28)
                                   .AddText($"\n{FormatRemaining(expiry.Value - DateTime.Now)}")
                                   .AddText("\n")
                                   .AddUiForeground("[月卡剩余]", 28)
                                   .AddText($"\n{FormatRemaining(record.MonthCardRemaining)}")
                                   .AddText("\n")
                                   .AddUiForeground("[点卡剩余]", 28)
                                   .AddText($"\n{FormatRemaining(record.PointCardRemaining)}")
                                   .AddText($"\n\n上次记录：{record.RecordedAt:yyyy-MM-dd HH:mm:ss}")
                                   .AddText("\n(左键: ")
                                   .AddUiForeground("设置界面", 34)
                                   .AddText(")")
                                   .AddText("\n(右键: ")
                                   .AddUiForeground("时长充值页面", 34)
                                   .AddText(")")
                                   .Build();

            this.dtrEntry.Shown = true;
        }
        catch (Exception ex)
        {
            Services.Log.Debug($"[SubTimeRelogin] 更新服务器信息栏失败: {ex.Message}");
        }
    }

    private static string FormatRemaining(TimeSpan? remaining)
    {
        if (remaining is null)
            return "无";

        var span = remaining.Value;
        if (span <= TimeSpan.Zero)
            return "已用尽";

        if (span.TotalDays >= 1)
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}天{span.Hours}小时");

        if (span.TotalHours >= 1)
            return string.Create(CultureInfo.InvariantCulture, $"{span.Hours}小时{span.Minutes}分");

        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}分");
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        Services.AddonLifecycle.UnregisterListener(this.OnAddonUpdate);

        if (this.dtrEntry != null)
        {
            try
            {
                this.dtrEntry.Remove();
            }
            catch (Exception ex)
            {
                Services.Log.Debug($"[SubTimeRelogin] 移除服务器信息栏条目失败: {ex.Message}");
            }
        }
    }
}
