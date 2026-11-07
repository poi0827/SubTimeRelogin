using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SubTimeRelogin;

/// <summary>
/// 插件用到的 Dalamud 服务集合。由 Plugin 的构造函数注入后统一保存，避免使用反射式注入。
/// </summary>
public static class Services
{
    public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    public static IClientState ClientState { get; private set; } = null!;

    public static IPlayerState PlayerState { get; private set; } = null!;

    public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    public static IFramework Framework { get; private set; } = null!;

    public static IDtrBar DtrBar { get; private set; } = null!;

    public static ICommandManager CommandManager { get; private set; } = null!;

    public static IChatGui ChatGui { get; private set; } = null!;

    public static IGameGui GameGui { get; private set; } = null!;

    public static IDataManager DataManager { get; private set; } = null!;

    public static ICondition Condition { get; private set; } = null!;

    public static IPluginLog Log { get; private set; } = null!;

    public static void Initialize(
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
        PluginInterface = pluginInterface;
        ClientState = clientState;
        PlayerState = playerState;
        AddonLifecycle = addonLifecycle;
        Framework = framework;
        DtrBar = dtrBar;
        CommandManager = commandManager;
        ChatGui = chatGui;
        GameGui = gameGui;
        DataManager = dataManager;
        Condition = condition;
        Log = log;
    }

    /// <summary>按名字取 Addon 指针，取不到返回 null。</summary>
    public static unsafe nint GetAddonAddress(string name)
    {
        try
        {
            return GameGui.GetAddonByName(name, 1);
        }
        catch (Exception ex)
        {
            Log.Debug($"[SubTimeRelogin] 读取 Addon {name} 失败: {ex.Message}");
            return 0;
        }
    }
}
