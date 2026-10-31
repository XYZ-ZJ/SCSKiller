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
            $"1. SCSKiller deletes this game's shader caches ({(Host.Platform.SystemShaderCache ? "the driver's, Windows' and" : "the driver's and")} the game's own) and starts the game. Load a save you can load again and play. The {minutes} minutes count from the game's first frame, loading and pauses included.",
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
        ToolTip.SetTip(overlay, "A small panel at the top right of the game: the time left, your frame times and, on DirectX 12 and Vulkan, each shader compilation stutter. SCSKiller counts down here either way. It isn't shown with FSR or XeSS frame generation on.");
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
                            Bullet("A small d3d11.dll goes next to the game for this recording only, then it's removed.", frames11),
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
            Content = "SCSKiller clears the caches and compiles again first, so the new run isn't helped by the last one. Then play the same save again.",
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
            Content = "Its shader caches were cleared, so the game stutters until it's compiled again.",
            PrimaryButtonText = "立即编译", SecondaryButtonText = "暂不编译", CloseButtonText = "继续测试", DefaultButton = ContentDialogButton.Primary,
        };
        var result = await App.ShowAsync(dialog);
        if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary)) return null;
        return await BeforeAfterUi.EndAsync(id, compile: result == ContentDialogResult.Primary);
    }
}
