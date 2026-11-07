using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace SubTimeRelogin;

/// <summary>
/// 读取国服客户端角色选择界面的订阅信息（月卡 / 点卡剩余秒数）。
///
/// 国际版的 LobbySubscriptionInfo（FFXIVClientStructs 中 Size = 0x40）只有
/// Flags / VeteranRewardRank / TotalDaysSubscribed / DaysRemaining / DaysUntilNextVeteranRank，
/// 国服客户端在同一块结构里另有裸字段：
///   偏移 0x10 = 月卡剩余秒数
///   偏移 0x18 = 点卡剩余秒数
/// DailyRoutines 的 AutoRecordSubTimeLeft 也正是用 Marshal.ReadInt64(ptr, 16/24) 读取这两个值。
/// </summary>
internal static unsafe class SubscriptionReader
{
    private const int MonthCardSecondsOffset = 0x10;
    private const int PointCardSecondsOffset = 0x18;

    /// <summary>月卡最长约 400 天，用于过滤无效内存。</summary>
    private const long MaxSaneSeconds = 400L * 24 * 60 * 60;

    public static bool TryRead(out long monthSeconds, out long pointSeconds)
    {
        monthSeconds = 0;
        pointSeconds = 0;

        var agent = AgentLobby.Instance();
        if (agent == null)
            return false;

        var uiClient = &agent->LobbyData.LobbyUIClient;
        var info = uiClient->SubscriptionInfo;
        if (info == null)
            return false;

        var month = *(long*)((byte*)info + MonthCardSecondsOffset);
        var point = *(long*)((byte*)info + PointCardSecondsOffset);

        if (!IsSane(month) || !IsSane(point))
            return false;

        monthSeconds = month;
        pointSeconds = point;
        return true;
    }

    private static bool IsSane(long seconds) => seconds is >= 0 and <= MaxSaneSeconds;

    /// <summary>当前值是否与记录一致（避免无意义的重复保存）。</summary>
    public static bool IsUnchanged(SubscriptionRecord? record, long monthSeconds, long pointSeconds)
        => record is not null
           && record.RecordedAt != DateTime.MinValue
           && record.SameAs(monthSeconds, pointSeconds);

    /// <summary>读取当前鼠标悬停角色的 ContentId，失败返回 0。</summary>
    public static ulong GetHoveredCharacterContentId()
    {
        var agent = AgentLobby.Instance();
        return agent == null ? 0 : agent->HoveredCharacterContentId;
    }

    /// <summary>读取当前选中的服务器 ID，失败返回 0。</summary>
    public static uint GetSelectedWorldId()
    {
        var agent = AgentLobby.Instance();
        return agent == null ? 0u : agent->WorldId;
    }

    /// <summary>读取角色选择界面当前选中的服务器下标（0-7），失败返回 -1。</summary>
    public static int GetWorldIndex()
    {
        var agent = AgentLobby.Instance();
        return agent == null ? -1 : agent->WorldIndex;
    }

    /// <summary>在角色选择列表中按 ContentId 查找角色下标，找不到返回 -1。</summary>
    public static int FindCharacterIndex(ulong contentId)
    {
        var agent = AgentLobby.Instance();
        if (agent == null || contentId == 0)
            return -1;

        var entries = agent->LobbyData.CharaSelectEntries;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i].Value;
            if (entry == null)
                continue;

            if (entry->ContentId == contentId)
                return i;
        }

        return -1;
    }

    /// <summary>在当前大区服务器列表里按世界 ID 找下标，找不到返回 -1。</summary>
    public static int FindWorldIndexById(uint worldId)
    {
        var agent = AgentLobby.Instance();
        if (agent == null || worldId == 0)
            return -1;

        var worlds = agent->LobbyData.LobbyUIClient.CurrentDataCenterWorlds;
        for (var i = 0; i < worlds.Count; i++)
        {
            if (worlds[i].Id == worldId)
                return worlds[i].Index;
        }

        return -1;
    }

    /// <summary>当前是否停留在角色选择界面（已读取到服务器列表）。</summary>
    public static bool IsOnCharacterSelect()
    {
        var agent = AgentLobby.Instance();
        return agent != null && agent->WorldIndex != -1;
    }
}
