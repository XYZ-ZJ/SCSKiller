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
            await AskSupportAsync(main);
        };
        main.Opened += Once;

        async void Once(object? _, EventArgs __)
        {
            main.Opened -= Once;
            if (AppStore.Moved == null && core.Settings.SupportFirstUse == null) SaveSupport(core, core.Settings with { SupportFirstUse = DateTimeOffset.UtcNow });
            await run();
        }
    }

    static bool dialogShown;

    static async Task<ContentDialogResult?> TryShowAsync(ContentDialog dialog)
    {
        if (App.Main.TryShowDialog(dialog) is not { } shown) return null;
        dialogShown = true;
        return await shown;
    }

    /// <summary>Checked once per opening, last. A skipped opening costs no try, and a try is spent as it shows: quitting from it counts as X.</summary>
    static async Task AskSupportAsync(Window main)
    {
        try
        {
            if (Host.Core is not ScsKiller k || AppStore.Moved != null || !SupportPopup.TimeToAsk(k.Settings, DateTimeOffset.UtcNow)) return;
            // true until a scan has listed the games, which the rule reads too
            for (var i = 0; i < SupportPopup.ListChecks && await Task.Run(() => k.GameRunning()); i++) await Task.Delay(SupportPopup.ListCheckEvery);
            var earned = await Task.Run(() => SupportPopup.Earned(k.Games, k.Store.LoadGame));
            var gameOpen = await Task.Run(() => k.GameRunning());
            // nothing awaits from here to the show
            var compiling = Busy.IsHeld() || k.Compiling || k.Queue.Any(q => !q.PlanCheck && Fmt.Active(q));
            if (dialogShown || !main.IsVisible || main.WindowState == WindowState.Minimized
                || !SupportPopup.Due(k.Settings, DateTimeOffset.UtcNow, App.Account.SignedIn, gameOpen, compiling, earned)
                || App.Main.TryShowDialog(SupportDialog()) is not { } shown) return;
            SaveSupport(k, SupportPopup.Asked(k.Settings, DateTimeOffset.UtcNow));
            var answer = await shown;
            if (answer == ContentDialogResult.None) return;
            SaveSupport(k, SupportPopup.Answered(k.Settings));
            if (answer == ContentDialogResult.Primary) await main.Launcher.LaunchUriAsync(new Uri(SupportPopup.Link));
        }
        catch (Exception e) { Host.Log?.Report($"the support popup failed: {e.Message}"); }
    }

    // a settings.json that can't be written costs a repeat of the popup, never the app or the answer on screen
    static void SaveSupport(IScsKiller core, Settings s)
    {
        try { core.Settings = s; }
        catch (Exception e) { Host.Log?.Report($"the support popup's state wasn't saved: {e.Message}"); }
    }

    /// <summary>Primary: the Patreon page, Secondary: no, each never asked again. X or Esc: <see cref="ContentDialogResult.None"/>.</summary>
    public static ContentDialog SupportDialog() => new()
    {
        Title = "喜欢 SCSKiller 吗？", Content = "如果它对您有所帮助，您可以在 Patreon 上支持开发团队。",
        PrimaryButtonText = "确实好用，前往支持", SecondaryButtonText = "不再提示",
        ShowsX = true, StackedButtons = true,
    };

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

    static bool askingOldPlans;

    /// <summary>Before a warm (<see cref="ScsKiller.OldPlansQuestion"/>): Delete and compile true, Keep it, Enter or Esc false, null when
    /// it wasn't shown or closed unanswered.</summary>
    public static Task<bool?> AskBeforeWarmAsync((string Title, string Text, bool Compile) n, CancellationToken close)
    {
        var answer = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Host.Ui.TryEnqueue(async () =>
        {
            try
            {
                if (close.IsCancellationRequested || !App.Main.IsVisible || App.Main.WindowState == WindowState.Minimized)
                {
                    answer.TrySetResult(null);
                    return;
                }
                var dialog = OldPlansDialog(n);
                using (close.Register(() => Host.Ui.TryEnqueue(dialog.Hide)))
                {
                    // a dialog open at the start, such as What's new, shows first: the wait for an answer runs meanwhile
                    var shown = App.Main.TryShowDialog(dialog);
                    while (shown == null && !close.IsCancellationRequested)
                    {
                        await Task.Delay(250);
                        if (!close.IsCancellationRequested) shown = App.Main.TryShowDialog(dialog);
                    }
                    dialogShown |= shown != null;
                    answer.TrySetResult(shown != null && await shown is var r && !close.IsCancellationRequested ? r == ContentDialogResult.Primary : null);   // Hide also ends it as None
                }
            }
            catch (Exception e) { answer.TrySetException(e); }
        })) answer.TrySetResult(null);
        return answer.Task;
    }

    /// <summary>A game with a lot of cache of its earlier versions (<see cref="ScsKiller.OldPlansNotice"/>), asked once: Delete takes
    /// those folders. Keep it or Esc keeps them.</summary>
    public static async Task AskOldPlansAsync(ScsKiller k)
    {
        if (askingOldPlans) return;
        askingOldPlans = true;
        try
        {
            var (id, notice) = await Task.Run(() => k.Games.Select(s => (s.Game.Id, k.OldPlansNotice(s.Game.Id))).FirstOrDefault(a => a.Item2 != null));
            if (notice is not { } n || await TryShowAsync(OldPlansDialog(n)) is not { } result) return;   // another dialog is open: asked again later
            var clear = result == ContentDialogResult.Primary;
            await Task.Run(() => k.OldPlansAnswered(id, keep: !clear));
            if (!clear) return;
            string? error;
            try { error = await Task.Run(() => k.DeleteOrphanedD3DSCache(id)) > 0 ? null : "No earlier versions' shader cache could be deleted."; }
            catch (Exception e) { error = e.Message; }
            if (error != null) await TryShowAsync(new ContentDialog { Title = "Couldn't clear the cache", Content = error, CloseButtonText = "OK" });
        }
        finally { askingOldPlans = false; }
    }

    public static ContentDialog OldPlansDialog((string Title, string Text, bool Compile) n) => new()
    {
        Title = n.Title, Content = new TextBlock { Text = n.Text, TextWrapping = TextWrapping.Wrap },
        PrimaryButtonText = n.Compile ? ScsKiller.OldPlansClear : ScsKiller.OldVersionsClear, CloseButtonText = ScsKiller.OldPlansKeep, DefaultButton = ContentDialogButton.Close,
    };

    static async Task<bool?> AskDiagnosticsAsync() => await TryShowAsync(new ContentDialog
    {
        Title = "分享诊断数据以协助解决问题？",
        Content = "当编译失败时发送错误代码（每次编译仅发送一次）。您可以在“设置”中更改此项。",
        PrimaryButtonText = "分享诊断数据", CloseButtonText = "暂不需要", DefaultButton = ContentDialogButton.Close,
    }) is { } r ? r == ContentDialogResult.Primary : null;
}
