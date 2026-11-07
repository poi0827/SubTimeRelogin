using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace SubTimeRelogin;

/// <summary>
/// 直接对客户端 Agent / Addon 做操作的小工具，用来替代 OmenTools 里的等价扩展方法。
/// </summary>
internal static unsafe class AgentHelper
{
    private const int LobbyDataOffset = 0x40;

    /// <summary>LobbyData.LobbyUIClient 在 AgentLobby 内的偏移（0x40 + 0x08）。</summary>
    public const int LobbyUIClientOffset = LobbyDataOffset + 0x08;

    public static AgentLobby* Lobby => AgentLobby.Instance();

    /// <summary>按名字取 AtkUnitBase 指针。</summary>
    public static AtkUnitBase* GetAddon(string name)
    {
        var address = Services.GetAddonAddress(name);
        return address == 0 ? null : (AtkUnitBase*)address;
    }

    /// <summary>
    /// 向登录 Agent 发送 UI 事件，等价于 AgentId.Lobby.SendEvent(...)。
    /// 登录界面用到的参数全部是 int。
    /// </summary>
    public static void SendLobbyEvent(ulong eventKind, params int[] args)
    {
        var agent = AgentLobby.Instance();
        if (agent == null)
            return;

        var argCount = args.Length;
        var values = stackalloc AtkValue[argCount == 0 ? 1 : argCount];
        for (var i = 0; i < argCount; i++)
        {
            values[i] = new AtkValue
            {
                Type = AtkValueType.Int,
                Int = args[i],
            };
        }

        var result = stackalloc AtkValue[1];
        agent->ReceiveEvent(result, values, (uint)argCount, eventKind);
    }

    /// <summary>点击「开始游戏」，进入角色选择。</summary>
    public static void OpenCharacterSelect() => SendLobbyEvent(0, 4);

    /// <summary>在角色选择界面切换服务器（index 为服务器列表下标）。</summary>
    public static void SelectWorldByIndex(int index) => SendLobbyEvent(0, 25, 0, index);

    /// <summary>在角色选择界面选中某个角色。</summary>
    public static void SelectCharacterByIndex(int index)
    {
        SendLobbyEvent(0, 21, index);
        SendLobbyEvent(3, 0);
    }

    public static bool IsAddonReady(AtkUnitBase* addon)
        => addon != null && addon->IsVisible && addon->IsFullyLoaded();

    public static bool IsAddonReady(string name) => IsAddonReady(GetAddon(name));

    /// <summary>
    /// 点击 Addon 内的按钮，等价于 OmenTools 的 AtkComponentButton.Click()。
    /// </summary>
    public static bool ClickButton(AtkUnitBase* addon, uint buttonId)
    {
        if (addon == null)
            return false;

        var button = addon->GetComponentButtonById(buttonId);
        if (button == null)
            return false;

        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null)
            return false;

        var evt = ownerNode->AtkResNode.AtkEventManager.Event;
        if (evt == null)
            return false;

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }

    /// <summary>把「请稍后再试」之类的登录临时锁清掉。</summary>
    public static void ClearTemporaryLock()
    {
        var agent = AgentLobby.Instance();
        if (agent != null && agent->TemporaryLocked)
            agent->TemporaryLocked = false;
    }
}
