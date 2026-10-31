using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui;

/// <summary>The first-run welcome, or a later start's one-time questions and What's new for a new version, once the
/// window first shows.</summary>
static class StartDialogs
{
    public static void WhenShown(Window main)
    {
        var core = Host.Core;
        Func<Task> run;
        if (!core.Settings.WelcomeSeen)
        {
            WhatsNew.AtStart(Host.Store, firstRun: true, AppVersion.Current);
            var fetch = Task.FromResult<WelcomeContent?>(WelcomeContent.Default);   // at most 3 s, from now rather than from the first show
            run = async () =>
            {
                if (await StartFlow.WelcomeAsync(core, () => AskWelcomeAsync(fetch))) App.SignInWithPatreon();
                await StartFlow.TraversalAsync(core, AskTraversalAsync);
            };
        }
        else run = async () =>
        {
            await StartFlow.QuestionsAsync(core, AskActiveCheckAsync, AskDiagnosticsAsync);
            await StartFlow.TraversalAsync(core, AskTraversalAsync);
            // recorded before it shows: if another dialog is up, About shows it
            if (WhatsNew.AtStart(Host.Store, firstRun: false, AppVersion.Current) is { } news) await TryShowAsync(WhatsNewDialog.Build(news));
        };
        main.Opened += Once;

        async void Once(object? _, EventArgs __)
        {
            main.Opened -= Once;
            await run();
        }
    }

    static async Task<ContentDialogResult?> TryShowAsync(ContentDialog dialog) => App.Main.TryShowDialog(dialog) is { } shown ? await shown : null;

    static async Task<WelcomeAnswer?> AskWelcomeAsync(Task<WelcomeContent?> fetch)
    {
        var (dialog, answer) = Welcome(await fetch ?? WelcomeContent.Default);
        return await TryShowAsync(dialog) is { } result ? answer(result) : null;
    }

    /// <summary>The server's text or the bundled one, as plain text.</summary>
    public static (ContentDialog Dialog, Func<ContentDialogResult, WelcomeAnswer> Answer) Welcome(WelcomeContent c)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var p in c.Paragraphs) panel.Children.Add(new TextBlock { Text = p, TextWrapping = TextWrapping.Wrap });
        if (c.Link != null) panel.Children.Add(new HyperlinkButton { Content = c.Link.Text, NavigateUri = new Uri(c.Link.Url), Padding = new(0) });
        var share = Box(c.Share);
        var diagnostics = Box("分享诊断数据以协助排查问题");
        var active = Box("发送匿名每日活跃检查", "帮助我们统计活跃安装量以规划服务器容量。每天发送一次，仅含软件版本号和显卡厂商信息，不含个人标识符。");
        panel.Children.AddRange([share, diagnostics, active]);
        var dialog = new ContentDialog { Title = c.Title, Content = panel, PrimaryButtonText = c.SignIn, CloseButtonText = c.Dismiss, DefaultButton = ContentDialogButton.Primary };
        return (dialog, result => new(share.IsChecked == true, diagnostics.IsChecked == true, active.IsChecked == true, result == ContentDialogResult.Primary));
    }

    // never pre-ticked: that isn't consent
    static CheckBox Box(string text, string? caption = null)
    {
        var label = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } } };
        if (caption != null) label.Children.Add(new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, Classes = { "Caption" } });
        var box = new CheckBox { IsChecked = false, Content = label };
        AutomationProperties.SetName(box, text);
        return box;
    }

    static async Task<bool?> AskActiveCheckAsync() => await TryShowAsync(ActiveCheckDialog()) is { } r ? r == ContentDialogResult.Primary : null;

    public static ContentDialog ActiveCheckDialog() => new()
    {
        Title = "匿名每日活跃检查",
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "SCSKiller 每天可以向我们的服务器发送一次匿名状态报告，仅包含软件版本号和显卡厂商信息，不含任何可识别身份的数据。 "
                        + "这有助于我们统计活跃安装量以合理规划服务器容量。默认保持关闭，除非您主动开启。",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = "您随时可以在“设置”中更改此项。", TextWrapping = TextWrapping.Wrap, Classes = { "Caption" } },
            },
        },
        PrimaryButtonText = "开启", CloseButtonText = "保持关闭", DefaultButton = ContentDialogButton.None,   // no nudge either way
    };

    static async Task<TraversalMode?> AskTraversalAsync() => await TryShowAsync(TraversalDialog(TraversalFix.LowVram(Host.Core.Vendor.Gpu))) is { } r
        ? r == ContentDialogResult.Primary ? TraversalMode.All : TraversalMode.Off : null;

    /// <summary>Primary: all games, Keep it off or Esc: off.</summary>
    public static ContentDialog TraversalDialog(bool? lowVram)
    {
        var panel = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = TraversalFix.AskIntro, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = TraversalFix.AskBody, TextWrapping = TextWrapping.Wrap } } };
        panel.Children.Add(lowVram == true
            ? new TextBlock { Text = TraversalFix.LowVramWarning, TextWrapping = TextWrapping.Wrap, [!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("WarnBrush") }
            : new TextBlock { Text = TraversalFix.VramLine, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = TraversalFix.AskFooter, TextWrapping = TextWrapping.Wrap, Classes = { "Caption" } });
        return new()
        {
            Title = TraversalFix.Title, Content = panel, PrimaryButtonText = TraversalFix.AskAll,
            CloseButtonText = TraversalFix.AskNotNow, DefaultButton = lowVram == true ? ContentDialogButton.Close : ContentDialogButton.None,
        };
    }

    static async Task<bool?> AskDiagnosticsAsync() => await TryShowAsync(new ContentDialog
    {
        Title = "分享诊断数据以协助解决问题？",
        Content = "当编译失败时发送错误代码（每次编译仅发送一次）。您可以在“设置”中更改此项。",
        PrimaryButtonText = "分享诊断数据", CloseButtonText = "暂不需要", DefaultButton = ContentDialogButton.Close,
    }) is { } r ? r == ContentDialogResult.Primary : null;
}
