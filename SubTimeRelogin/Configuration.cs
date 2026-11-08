using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace SubTimeRelogin;

/// <summary>
/// 一次「剩余游戏时间」的读取快照。
/// 国服客户端的 LobbySubscriptionInfo 中，偏移 0x10 存放月卡剩余秒数，偏移 0x18 存放点卡剩余秒数。
/// </summary>
public sealed class SubscriptionRecord
{
    public DateTime RecordedAt { get; set; } = DateTime.MinValue;

    /// <summary>月卡剩余秒数（0 表示没有月卡）。</summary>
    public long MonthCardSeconds { get; set; }

    /// <summary>点卡剩余秒数（0 表示没有点卡）。</summary>
    public long PointCardSeconds { get; set; }

    public bool HasMonthCard => MonthCardSeconds > 0;
    public bool HasPointCard => PointCardSeconds > 0;

    public DateTime? MonthCardExpiry => RecordedAt == DateTime.MinValue || MonthCardSeconds <= 0
        ? null
        : RecordedAt.AddSeconds(MonthCardSeconds);

    public DateTime? PointCardExpiry => RecordedAt == DateTime.MinValue || PointCardSeconds <= 0
        ? null
        : RecordedAt.AddSeconds(PointCardSeconds);

    public TimeSpan? MonthCardRemaining
    {
        get
        {
            var expiry = MonthCardExpiry;
            if (expiry == null) return null;
            var left = expiry.Value - DateTime.Now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public TimeSpan? PointCardRemaining
    {
        get
        {
            var expiry = PointCardExpiry;
            if (expiry == null) return null;
            var left = expiry.Value - DateTime.Now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public bool SameAs(long monthSeconds, long pointSeconds)
        => MonthCardSeconds == monthSeconds && PointCardSeconds == pointSeconds;

    public SubscriptionRecord Clone() => new()
    {
        RecordedAt = RecordedAt,
        MonthCardSeconds = MonthCardSeconds,
        PointCardSeconds = PointCardSeconds,
    };
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ── 自动重连 ────────────────────────────────────────────────
    /// <summary>掉线后是否自动重连。</summary>
    public bool EnableAutoRelogin { get; set; } = true;

    /// <summary>只有月卡仍有剩余时间时才自动重连（需求核心）。</summary>
    public bool RequireActiveMonthCard { get; set; } = true;

    /// <summary>没有读到过任何剩余时间数据时，是否也允许重连。</summary>
    public bool ReloginWithoutSubscriptionData { get; set; } = false;

    /// <summary>两次重连尝试之间的间隔（秒）。</summary>
    public int RetryIntervalSeconds { get; set; } = 10;

    /// <summary>最大尝试次数，0 = 一直重试。</summary>
    public int MaxReloginAttempts { get; set; } = 0;

    /// <summary>重连时清除「请稍后再试」登录锁（TemporaryLocked）。</summary>
    public bool IgnoreLoginLock { get; set; } = true;

    /// <summary>重连时自动确认登录错误/排队提示框。</summary>
    public bool AutoAcceptDialogue { get; set; } = true;

    // ── 显示 ────────────────────────────────────────────────────
    public bool ShowDtrEntry { get; set; } = true;
    public bool ShowRemainingOnCharacterSelect { get; set; } = true;

    // ── 记忆 ────────────────────────────────────────────────────
    public ulong LastContentId { get; set; }
    public uint LastWorldId { get; set; }
    public string LastCharacterName { get; set; } = string.Empty;

    /// <summary>账号级（最近一次）的剩余时间快照。</summary>
    public SubscriptionRecord? AccountRecord { get; set; }

    /// <summary>按角色 ContentId 保存的剩余时间快照。</summary>
    public Dictionary<ulong, SubscriptionRecord> Records { get; set; } = new();

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface) => this.pluginInterface = pluginInterface;

    public void Save() => this.pluginInterface?.SavePluginConfig(this);

    /// <summary>
    /// 取用于判定重连的那个快照：所有记录里「记录时间最新」的那一条。
    ///
    /// 订阅时间每次登录只在角色选择界面读一次，按角色存的旧记录可能比
    /// 本次登录读到的账号级记录更老，若优先返回旧记录会让月卡判定用过期的数据，
    /// 所以这里统一按 RecordedAt 取最新。
    /// </summary>
    public SubscriptionRecord? GetBestRecord()
    {
        SubscriptionRecord? best = this.AccountRecord;

        if (this.LastContentId != 0
            && this.Records.TryGetValue(this.LastContentId, out var own)
            && (best is null || own.RecordedAt > best.RecordedAt))
            best = own;

        foreach (var record in this.Records.Values)
        {
            if (best is null || record.RecordedAt > best.RecordedAt)
                best = record;
        }

        return best is not null && best.RecordedAt != DateTime.MinValue ? best : null;
    }
}
