using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

public sealed partial class LibraryPage : UserControl
{
    public LibraryVm Vm { get; } = new();
    readonly Coalesced refresh, step;
    bool shiftOnPress;

    public LibraryPage()
    {
        InitializeComponent();
        DataContext = Vm;
        refresh = new(Host.Ui, () => { Vm.Refresh(); FitRowsSoon(); });
        step = new(Host.Ui, () => { Vm.ScanStep(); FitRowsSoon(); });
        // MenuFlyout's presenter keeps the list Items holds when it is first built, before Opening: create it now
        ((MenuFlyout)FoldersButton.Flyout!).Items.Clear();
        // a refresh's Shift: the press's, by mouse or keyboard
        RefreshButton.AddHandler(PointerPressedEvent, (_, e) => shiftOnPress = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel, handledEventsToo: true);
        RefreshButton.AddHandler(KeyDownEvent, (_, e) => shiftOnPress = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel, handledEventsToo: true);
        Columns.SizeChanged += (_, e) => { if (e.WidthChanged) FitRowsSoon(); };
        Rows.SizeChanged += (_, _) => FitRowsSoon();
        PropertyChanged += (_, e) => { if (e.Property == FontSizeProperty) FitRowsSoon(); };
    }

    void FitRowsSoon() => Dispatcher.UIThread.Post(FitRows, DispatcherPriority.Loaded);

    /// <summary>Narrow when beside the game and its numbers a row's buttons would leave its status under 8 em.</summary>
    void FitRows()
    {
        Page.VerticalScrollBarVisibility = FontSize > 14 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        Toolbar.Classes.Set("Stacked", Toolbar.Bounds.Width < FontSize * 56);   // 100% never stacks, even at the minimum window width
        var c = Columns.ColumnDefinitions;
        var buttons = Table.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("RowButtons"))
            .Select(p => p.Children.Where(b => b.IsVisible).Sum(b => b.DesiredSize.Width + p.Spacing)).DefaultIfEmpty().Max();
        var need = c[0].MinWidth + c.Skip(1).Take(4).Sum(d => d.ActualWidth) + 5 * Columns.ColumnSpacing + 12 + buttons + 12 + FontSize * 8;
        bool narrow = need > Columns.Bounds.Width;
        if (narrow == Table.Classes.Contains("Narrow")) return;
        Table.Classes.Set("Narrow", narrow);
        (Table.Resources["StatusWidth"], Table.Resources["StatusMinWidth"]) = narrow ? (new GridLength(0), 0.0) : (new GridLength(4.1, GridUnitType.Star), 355.0);
    }

    void OnChanged(GameState _) => refresh.Request();
    void OnQueue(QueueItem q) { if (!Format.ProgressTick(q)) refresh.Request(); }   // progress ticks change nothing here

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Host.Core.GameChanged += OnChanged; Host.Core.QueueChanged += OnQueue; Host.Icons.Changed += refresh.Request;
        if (Host.Core is ScsKiller k) k.ScanProgressed += step.Request;
        Vm.Load();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        Host.Core.GameChanged -= OnChanged; Host.Core.QueueChanged -= OnQueue; Host.Icons.Changed -= refresh.Request;
        if (Host.Core is ScsKiller k) k.ScanProgressed -= step.Request;
    }

    void OnSearch(object? _, TextChangedEventArgs __) => Vm.Filter = Search.Text ?? "";

    void OnSearchKey(object? _, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || string.IsNullOrEmpty(Search.Text)) return;
        Search.Text = "";
        e.Handled = true;
    }

    /// <summary>--screenshots: a search typed, and the list scrolled to its last section or to a game.</summary>
    public string SearchText { set => Search.Text = value; }
    public void ScrollToEnd() { if (Vm.Games.Count > 0) Scroller.ScrollToEnd(); }
    public void ScrollToTop() => Scroller.Offset = default;
    public void ScrollTo(string id)
    {
        if (RowButtons().FirstOrDefault(b => ((GameRow)b.DataContext!).Id == id) is { } row && row.TranslatePoint(default, (Visual)Scroller.Content!) is { } at)
            Scroller.Offset = new(0, at.Y);
    }

    ScrollViewer Scroller => Page.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled ? List : Page;

    IEnumerable<Button> RowButtons() => List.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("Row") && b.DataContext is GameRow);

    static GameRow RowOf(object? sender) => (GameRow)((Control)sender!).DataContext!;

    // a row's own buttons raise Click through the row: only a click on the row itself opens the game
    void OnGameClick(object? sender, RoutedEventArgs e) { if (e.Source == sender) App.Main.Navigate(typeof(DetailPage), RowOf(sender).Id); }
    void OnDetails(object? sender, RoutedEventArgs e) { e.Handled = true; App.Main.Navigate(typeof(DetailPage), RowOf(sender).Id); }
    void OnSettings(object? _, RoutedEventArgs __) => App.Main.Navigate(typeof(SettingsPage));
    void OnCache(object? _, RoutedEventArgs __) => App.Main.Navigate(typeof(CachePage));
    void OnPlay(object? sender, RoutedEventArgs e) { e.Handled = true; Vm.Play(RowOf(sender)); }
    void OnShowGame(object? sender, RoutedEventArgs _) => Vm.ShowInLibrary((HiddenRow)((Control)sender!).DataContext!);

    // Either click fetches the server's data whatever its age; Shift+click also forces re-detection, which is otherwise
    // redone only when an exe, build or SCSKiller changes.
    void OnRefresh(object? _, RoutedEventArgs __)
    {
        Vm.Rescan(force: shiftOnPress, userRequested: true);
        shiftOnPress = false;
    }

    // Adding only queues the game: nothing compiles until Compile queue.
    void OnAdd(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Host.Core.Enqueue(RowOf(sender).Id);
        Vm.Refresh();
        FitRowsSoon();
    }

    void OnDismissGpuNotice(object? _, RoutedEventArgs __) => Vm.DismissGpuNotice();

    void OnSortName(object? _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.NameAscending, LibrarySort.NameDescending);
    void OnSortPipelines(object? _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.PipelinesDescending, LibrarySort.PipelinesAscending);
    void OnSortCache(object? _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.CacheDescending, LibrarySort.CacheAscending);
    void OnSortStatus(object? _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.Status, LibrarySort.Status);

    // Built as it opens: the folders may have changed since.
    void OnFoldersOpening(object? _, EventArgs __)
    {
        var menu = (MenuFlyout)FoldersButton.Flyout!;
        menu.Items.Clear();
        var add = new MenuItem { Header = "Add a folder of games…", Icon = Glyph(Glyphs.Add) };
        add.Click += OnAddFolder;
        menu.Items.Add(add);
        var leave = new MenuItem { Header = "Leave out a folder…", Icon = Glyph(Glyphs.Blocked) };
        ToolTip.SetTip(leave, "安装在其中的游戏将不会被扫描或读取");
        leave.Click += OnLeaveOutFolder;
        menu.Items.Add(leave);
        var folders = Fmt.Added?.Folders ?? [];
        if (folders.Count > 0) menu.Items.Add(new Separator());
        foreach (var folder in folders)
        {
            // a header's "_" marks an access key: doubled, it shows
            var item = new MenuItem { Header = folder.Replace("_", "__"), Icon = Glyph(Glyphs.Dismiss) };
            ToolTip.SetTip(item, "停止在此处查找游戏");
            item.Click += async (_, _) =>
            {
                if (!await App.ConfirmAsync("停止在此处查找游戏？",
                        $"{folder}\n\nThe games SCSKiller found only in this folder leave the library. Nothing in the folder changes.", "停止查找")) return;
                if (Fmt.Added is { } added) await Task.Run(() => added.RemoveFolder(folder));
                Vm.Rescan(force: false);
            };
            menu.Items.Add(item);
        }
        var leftOut = Host.Core.Settings.LeftOutFolders ?? [];
        if (leftOut.Count > 0) menu.Items.Add(new Separator());
        foreach (var folder in leftOut)
        {
            var item = new MenuItem { Header = $"Left out: {folder.Replace("_", "__")}", Icon = Glyph(Glyphs.Refresh) };
            ToolTip.SetTip(item, "重新在此处查找游戏");
            item.Click += async (_, _) =>
            {
                await Task.Run(() => Host.Core.ScanFolderAgain(folder));
                Vm.Rescan(force: false);
            };
            menu.Items.Add(item);
        }
    }

    async void OnLeaveOutFolder(object? _, RoutedEventArgs __)
    {
        string? path;
        try { path = (await App.Main.StorageProvider.OpenFolderPickerAsync(new() { Title = "排除此文件夹" })).FirstOrDefault()?.TryGetLocalPath(); }
        catch (Exception ex) { await Message("无法打开文件夹选择器", ex.Message); return; }
        if (path == null) return;
        try { await Task.Run(() => Host.Core.LeaveOutFolder(path)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Message("无法排除此文件夹", ex.Message);
            return;
        }
        Vm.Rescan(force: false);
    }

    static TextBlock Glyph(string glyph) => new() { Classes = { "Icon" }, Text = glyph };

    // Each folder in it is taken for one game, its exe found as for a store's install
    async void OnAddFolder(object? _, RoutedEventArgs __)
    {
        string? path;
        try { path = (await App.Main.StorageProvider.OpenFolderPickerAsync(new() { Title = "在此处查找游戏" })).FirstOrDefault()?.TryGetLocalPath(); }
        catch (Exception ex) { await Message("无法打开文件夹选择器", ex.Message); return; }
        if (path == null || Fmt.Added is not { } added) return;
        try { await Task.Run(() => added.AddFolder(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await Message("无法添加此文件夹", ex.Message);
            return;
        }
        Vm.Rescan(force: false);
    }

    // A launcher stub is resolved to the game's own exe, shown with the game folder to confirm; an exe of a listed game
    // opens that game instead.
    async void OnAddGame(object? _, RoutedEventArgs __)
    {
        string? path;
        try
        {
            var picked = await App.Main.StorageProvider.OpenFilePickerAsync(new() { Title = "添加游戏", FileTypeFilter = [new(".exe") { Patterns = ["*.exe"] }] });
            path = picked.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception ex) { await Message("无法打开文件选择器", ex.Message); return; }
        if (path == null) return;
        ManualAdd added;
        try { added = await Task.Run(() => Host.Core.PreviewManualGame(path)); }   // reads the install's files
        catch (Exception ex)   // a rejection's message, or a data folder SCSKiller couldn't read
        {
            await Message("无法添加此游戏", ex.Message);
            return;
        }
        if (!added.Existed)
        {
            if (await GameFolderDialog.ShowAsync(added.Game, $"添加《{added.Game.Name}》", "添加游戏") is not { } folder) return;
            try { await Task.Run(() => Host.Core.AddManualGame(path, folder)); }
            catch (Exception ex)
            {
                await Message("无法添加此游戏", ex.Message);
                return;
            }
            Vm.Rescan(force: false);   // lists it under "Added by you" once its engine and anti-cheat are checked
            return;
        }
        if (Host.Core.HiddenGames.Any(g => g.Id == added.Game.Id))
        {
            await Message("已从游戏库中隐藏", $"您之前移除了《{added.Game.Name}》。勾选“显示已隐藏”可重新显示。");
            return;
        }
        var listed = Host.Core.Games.Any(s => s.Game.Id == added.Game.Id);
        var where = added.Game.Store == Store.Manual ? "您已添加过此游戏" : $"SCSKiller 已在 {Fmt.StoreName(added.Game)} 中找到它";
        if (listed && await App.ConfirmAsync("已在游戏库中", $"《{added.Game.Name}》已在列表中：{where}。", "前往查看", ContentDialogButton.Primary))
            App.Main.Navigate(typeof(DetailPage), added.Game.Id);
        else if (!listed) await Message("已在游戏库中", $"《{added.Game.Name}》：{where}。请刷新游戏库查看。");
    }

    static Task Message(string title, string text) => App.ShowAsync(new ContentDialog { Title = title, Content = text, CloseButtonText = "确定" });

    void OnAddAll(object? _, RoutedEventArgs __) => Vm.AddAllReady();
    void OnAddRecommended(object? _, RoutedEventArgs __) => Vm.AddAllRecommended();

    void OnCompileQueue(object? _, RoutedEventArgs __)
    {
        App.CompileQueue();
        App.Main.Navigate(typeof(QueuePage));
    }

    // Unsupported: say why; an encrypted game can be unlocked with its AES key (stored on this PC only).
    async void OnWhy(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = RowOf(sender);
        var title = $"Why can't {row.Name} be compiled?";
        if (!ScsKiller.NeedsAesKey(row.State))
            await App.ShowAsync(new ContentDialog { Title = title, Content = row.Reason, CloseButtonText = "Close" });
        else if (await AesKeyDialog.ShowAsync(row.Id, title, row.Reason)) Vm.Rescan();
    }

    async void OnRecord(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = RowOf(sender);
        if (!await App.ConfirmAsync($"Record {row.Name}?",
                $"SCSKiller adds a small {Fmt.RecorderFiles(row.State)} next to the game that writes down every pipeline it creates. " +
                "Play for about 5 minutes, close the game, then add it to the compile queue. You can remove it any time from the game's page.",
                "Add recorder", ContentDialogButton.Primary)) return;
        try { await Task.Run(() => Host.Core.InstallRecorder(row.Id)); }   // file IO and the game's re-evaluation
        catch (Exception ex) { await App.ShowAsync(new ContentDialog { Title = "Couldn't add the recorder", Content = ex.Message, CloseButtonText = "OK" }); }
        Vm.Refresh();
        FitRowsSoon();
    }
}
