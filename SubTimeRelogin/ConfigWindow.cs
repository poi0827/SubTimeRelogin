using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SubTimeRelogin;

/// <summary>
/// 插件设置窗口。
/// </summary>
internal sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly Func<string> statusProvider;
    private readonly Action onChanged;

    public ConfigWindow(Configuration config, Func<string> statusProvider, Action onChanged)
        : base("SubTimeRelogin 设置###SubTimeReloginConfig")
    {
        this.config = config;
        this.statusProvider = statusProvider;
        this.onChanged = onChanged;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 320),
            MaximumSize = new Vector2(1000, 1000),
        };
    }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f), "订阅剩余时间记录");
        ImGui.TextWrapped(this.statusProvider());
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f), "自动重连");
        ImGui.Spacing();

        var enableRelogin = this.config.EnableAutoRelogin;
        if (ImGui.Checkbox("启用掉线自动重连", ref enableRelogin))
        {
            this.config.EnableAutoRelogin = enableRelogin;
            this.onChanged();
        }

        var requireMonthCard = this.config.RequireActiveMonthCard;
        if (ImGui.Checkbox("仅在月卡仍有剩余时间时重连", ref requireMonthCard))
        {
            this.config.RequireActiveMonthCard = requireMonthCard;
            this.onChanged();
        }

        ImGui.BeginDisabled(!this.config.RequireActiveMonthCard);
        var withoutData = this.config.ReloginWithoutSubscriptionData;
        if (ImGui.Checkbox("没有订阅记录时也重连", ref withoutData))
        {
            this.config.ReloginWithoutSubscriptionData = withoutData;
            this.onChanged();
        }

        ImGui.EndDisabled();

        var ignoreLock = this.config.IgnoreLoginLock;
        if (ImGui.Checkbox("忽略「请稍后再试」登录锁", ref ignoreLock))
        {
            this.config.IgnoreLoginLock = ignoreLock;
            this.onChanged();
        }

        var autoDialogue = this.config.AutoAcceptDialogue;
        if (ImGui.Checkbox("自动确认登录错误提示框", ref autoDialogue))
        {
            this.config.AutoAcceptDialogue = autoDialogue;
            this.onChanged();
        }

        var retry = this.config.RetryIntervalSeconds;
        if (ImGui.SliderInt("重试间隔（秒）", ref retry, 3, 120))
        {
            this.config.RetryIntervalSeconds = retry;
            this.onChanged();
        }

        var maxAttempts = this.config.MaxReloginAttempts;
        if (ImGui.SliderInt("最大重试次数（0 = 不限）", ref maxAttempts, 0, 100))
        {
            this.config.MaxReloginAttempts = maxAttempts;
            this.onChanged();
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f), "显示");
        ImGui.Spacing();

        var showDtr = this.config.ShowDtrEntry;
        if (ImGui.Checkbox("在服务器信息栏显示剩余时间", ref showDtr))
        {
            this.config.ShowDtrEntry = showDtr;
            this.onChanged();
        }

        var showRemain = this.config.ShowRemainingOnCharacterSelect;
        if (ImGui.Checkbox("在角色选择界面显示剩余时间", ref showRemain))
        {
            this.config.ShowRemainingOnCharacterSelect = showRemain;
            this.onChanged();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("保存并关闭"))
        {
            this.config.Save();
            this.IsOpen = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("清除订阅记录"))
        {
            this.config.Records.Clear();
            this.config.AccountRecord = null;
            this.config.Save();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("设置会自动保存");
    }
}
