using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

/// <summary>The before and after recording's dialogs, which the game page opens; the actions behind them are
/// <see cref="BeforeAfterUi"/>'s.</summary>
static class BeforeAfterDialogs
{
    public enum Intro { Start, StartOver, HowItWorks }

    /// <summary>Screenshots: the intro dialog while it's open.</summary>
    public static ContentDialog? Showing { get; private set; }

    /// <summary>The intro (doc 6.5): the steps (folded behind "How it works" once seen), the play time and the sound.
    /// Null = cancelled, or <see cref="Intro.HowItWorks"/>. <paramref name="frames11"/>: the pair puts a frames-only
    /// d3d11.dll into the game (<see cref="ScsKiller.Frames11"/>). <paramref name="on"/>: the window it shows over, the
    /// main one by default. <paramref name="max"/>: Max compile is offered for the after run.</summary>
    public static async Task<(int Minutes, bool Sound, bool Overlay, bool Max)?> IntroAsync(string game, Intro mode, bool frames11 = false, DialogLayer? on = null, bool max = false)
    {
        var s = Host.Core.Settings;
        bool full = mode == Intro.HowItWorks || !s.BeforeAfterIntroSeen;
        int minutes = 5;
        static TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
        var (step1, step3) = (Line(""), Line(""));
        void Steps() => (step1.Text, step3.Text) = (
            $"1. SCSKiller 会删除该游戏的着色器缓存（{(Host.Platform.SystemShaderCache ? "包含显卡驱动缓存、Windows 缓存及" : "包含显卡驱动缓存与")}游戏自身缓存）并启动游戏。请加载一个可重复加载的存档并开始游玩。{minutes} 分钟计时将从游戏第一帧开始计算，包含加载与暂停时间。",
            $"3. 加载相同的存档并游玩相同的 {minutes} 分钟。");
        Steps();
        var steps = new StackPanel
        {
            Spacing = 8, IsVisible = full,
            Children = { step1, Line("2. 关闭游戏。SCSKiller 会再次清理缓存并完整编译该游戏的着色器。"), step3,
                Line("4. 生成一份对比测试报告，您可以保存或分享给他人。") },
        };
        var how = new HyperlinkButton { Content = "工作原理与说明", Padding = new(0), IsVisible = !full };
        how.Click += (_, _) => (steps.IsVisible, how.IsVisible) = (true, false);

        var group = $"play-time-{Guid.NewGuid():N}";
        var (five, ten) = (new RadioButton { Content = "5 分钟", GroupName = group, IsChecked = true }, new RadioButton { Content = "10 分钟", GroupName = group });
        ten.IsCheckedChanged += (_, _) => { minutes = ten.IsChecked == true ? 10 : 5; Steps(); };
        var time = new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = "游玩时长" }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { five, ten } } },
        };
        var sound = new CheckBox { Content = "时间到达时播放提示音", IsChecked = s.BeforeAfterSound };
        var overlay = new CheckBox { Content = "在游戏内显示倒计时浮窗", IsChecked = s.BeforeAfterOverlay };
        ToolTip.SetTip(overlay, "游戏右上角的小浮窗：显示剩余时间、实时帧时间，以及在 DirectX 12 和 Vulkan 下的每次着色器编译卡顿。无论是否开启浮窗，SCSKiller 都会在后台进行倒计时。若开启了 FSR 或 XeSS 帧生成，浮窗将自动隐藏。");
        var maxRun = new CheckBox { Content = "在编译后测试中采用极限模式编译", IsChecked = s.BeforeAfterMax, IsVisible = max };
        ToolTip.SetTip(maxRun, DetailVm.MaxCompileWhy);
        var checks = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { sound, overlay, maxRun } };
        var choices = new Grid { ColumnSpacing = 24, ColumnDefinitions = new("*,Auto"), Children = { time, checks }, IsVisible = mode != Intro.HowItWorks };
        Grid.SetColumn(checks, 1);

        static TextBlock Bullet(string text, bool shown = true) => new() { Text = "• " + text, TextWrapping = TextWrapping.Wrap, Classes = { "Secondary" }, IsVisible = shown };
        var dialog = new ContentDialog
        {
            Title = mode == Intro.StartOver ? $"重新开始 {game} 的对比测试？" : "编译前后对比测试",
            Content = new StackPanel
            {
                Spacing = 16, MaxWidth = 560,
                Children =
                {
                    new InfoBar
                    {
                        IsClosable = false, Severity = InfoBarSeverity.Warning,
                        Message = "此操作还会删除游戏自身的着色器缓存（未作备份且可能达 2 GB）。游戏在启动时将重新构建这些缓存，可能需要几分钟。",
                    },
                    steps, how, choices,
                    new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            Bullet("两次测试之间请勿更改画质图像设置。"),
                            Bullet("该游戏当前的编译缓存将被删除，并将在第 2 步中重新编译。"),
                            Bullet("仅在此次录制期间在游戏目录旁临时添加小巧的 d3d11.dll，测试结束后将立即移除。", frames11),
                            Bullet("帧时间数据仅保存在此电脑上。若开启数据分享，仅会发送匿名哈希。"),
                        },
                    },
                },
            },
            CloseButtonText = mode == Intro.HowItWorks ? "关闭" : "取消",
            PrimaryButtonText = mode == Intro.HowItWorks ? "" : "开始编译前测试",
            DefaultButton = mode == Intro.HowItWorks ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        Showing = dialog;
        var result = await App.ShowAsync(dialog, on);
        Showing = null;
        if (mode == Intro.HowItWorks || result != ContentDialogResult.Primary) return null;
        Host.Core.Settings = Host.Core.Settings with { BeforeAfterIntroSeen = true, BeforeAfterSound = sound.IsChecked == true, BeforeAfterOverlay = overlay.IsChecked == true, BeforeAfterMax = max ? maxRun.IsChecked == true : s.BeforeAfterMax };
        return (minutes, sound.IsChecked == true, overlay.IsChecked == true, max && maxRun.IsChecked == true);
    }

    /// <summary>The intro, then the before run: the caches cleared and the game started through its store. The error to
    /// show, or null (also when cancelled).</summary>
    public static async Task<string?> StartAsync(string id, bool over = false, DialogLayer? on = null)
    {
        var g = Host.Core.Games.First(x => x.Game.Id == id);
        bool frames11 = ScsKiller.Frames11(g.Engine, Host.Core.Vendor.Caps);
        bool max = ScsKiller.MaxCompileAdds(Host.Core.Vendor.Caps, g.Engine);
        if (await IntroAsync(g.Game.Name, over ? Intro.StartOver : Intro.Start, frames11, on, max) is not { } choice) return null;
        return await BeforeAfterUi.BeginAsync(id, choice.Minutes, choice.Overlay, over, choice.Max);
    }

    public static async Task<string?> RecordAfterAgainAsync(string id, DialogLayer? on = null)
    {
        var dialog = new ContentDialog
        {
            Title = "重新录制编译后测试？",
            Content = "SCSKiller 会先清空缓存并重新编译，确保新的测试不会受到上次运行的缓存影响。随后请重新加载相同的存档进行游玩。",
            PrimaryButtonText = "重新录制", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        if (await App.ShowAsync(dialog, on) != ContentDialogResult.Primary) return null;
        return await BeforeAfterUi.RecordAfterAgainAsync(id);
    }

    /// <summary>Cancel in progress: the caches were cleared, so "Compile now" is the default.</summary>
    public static async Task<string?> CancelAsync(string id)
    {
        var dialog = new ContentDialog
        {
            Title = "取消编译前后对比测试？",
            Content = "该游戏的着色器缓存已被清除，若不重新编译，游玩时可能会出现卡顿。",
            PrimaryButtonText = "立即编译", SecondaryButtonText = "暂不编译", CloseButtonText = "继续测试", DefaultButton = ContentDialogButton.Primary,
        };
        var result = await App.ShowAsync(dialog);
        if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary)) return null;
        return await BeforeAfterUi.EndAsync(id, compile: result == ContentDialogResult.Primary);
    }
}
