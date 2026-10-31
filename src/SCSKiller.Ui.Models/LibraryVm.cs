using System.Collections.ObjectModel;
using System.ComponentModel;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Ui.Models;

/// <summary>A small tag on a library row (a middleware label, or "+N").</summary>
public sealed record TagChip(string Text, string Tip, double Width = 90);   // Width: the text's most, trimmed past it

/// <summary>A game's row in the Library. The list keeps one row per game and <see cref="Update"/>s it in place, so the
/// row's container, the scroll position and hover survive a re-evaluation.</summary>
public sealed class GameRow(GameState s, bool queued = false, bool compiling = false) : Bindable
{
    static readonly uint[] Tiles = [0xFF3B4A8C, 0xFF6B3B5E, 0xFF2F6B4F, 0xFF2F5F73, 0xFF7A4A2A, 0xFF5A6B2F, 0xFF3D5566, 0xFF5C3B7A];

    public GameState State => s;
    public bool Playing => s.Playing;
    public string Id => s.Game.Id;
    public string Name => s.Game.Name;
    public object? Icon { get; private set; } = Host.Icons.Get(s.Game.ExePath);
    public bool NoIcon => Icon is null;
    /// <summary>The initials' tile colour, ARGB.</summary>
    public uint Tile { get; } = TileOf(s.Game.Name);
    public string Initials => InitialsOf(s.Game.Name);
    public static uint TileOf(string name) => Tiles[name.Sum(c => c) % Tiles.Length];
    public static string InitialsOf(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsUpper(w[0]) || char.IsDigit(w[0])).ToArray();
        return words.Length >= 2 ? $"{words[0][0]}{words[1][0]}" : name[..Math.Min(2, name.Length)].ToUpperInvariant();
    }
    public string Sub => $"{Fmt.Engine(s.Engine)} · {StoreName}";
    public string StoreName { get; } = Fmt.StoreName(s.Game);
    /// <summary>Search: the name, the store as players name it, or the engine ("UE 5", "Unreal"), case-insensitive.</summary>
    public bool Matches(string term) => Name.Contains(term, StringComparison.OrdinalIgnoreCase) || StoreName.Contains(term, StringComparison.OrdinalIgnoreCase)
        || Fmt.Engine(s.Engine).Contains(term, StringComparison.OrdinalIgnoreCase) || s.Engine?.Family.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    public bool HasTags => s.Middleware is { Count: > 0 } || ModChip(s) != null || ScsKiller.DeliveryChip(s) != null;
    public bool HasTagLine => HasTags || IsKnownStutter || IsNoStutter || HasBaChip;
    /// <summary>A before and after recording in progress; the tile has no button for it, the game page has.</summary>
    public string BaChip => BeforeAfterUi.Voided(s.BeforeAfter) ? "" : s.BeforeAfter?.Stage switch
    {
        BeforeAfterStage.AfterReady => "编译后测试已就绪",
        BeforeAfterStage.Compiling => "对比测试：正在编译",
        BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying => "对比测试：正在游玩",
        _ => "",
    };
    public bool HasBaChip => BaChip.Length > 0;
    public bool IsKnownStutter => s.KnownStutter != null;
    public string? StutterTip => Fmt.StutterTip(s);
    /// <summary>On game-verdicts.json as no-stutter: compiling stays possible, but the bulk adds leave it out.</summary>
    public bool IsNoStutter => s.NoStutter != null;
    public string? NoStutterTip => s.NoStutter;
    // The shader mod, then the upscalers: the first three, then "+N" with the rest in its tooltip. After the stutter chip
    // only one more chip fits before the Shaders column: the mod, else several upscalers become "N upscalers".
    const int MaxTags = 3;
    public List<TagChip> Tags { get; private set; } = TagsOf(s);
    static List<TagChip> TagsOf(GameState s)
    {
        var m = s.Middleware ?? [];
        var mod = ModChip(s);
        var asd = ScsKiller.DeliveryChip(s) is { } d ? new TagChip(d, ScsKiller.DeliveryTip, 190) : null;
        List<TagChip> tags = [.. mod != null ? [mod] : Array.Empty<TagChip>(), .. asd != null ? [asd] : Array.Empty<TagChip>(), .. m.Select(t => new TagChip(t.Label, Fmt.TagTip(t)))];
        return s.KnownStutter != null || s.NoStutter != null ? tags.Count > 1 ? [mod ?? asd ?? new TagChip($"{m.Count} upscalers", string.Join("\n", m.Select(Fmt.TagTip)))] : tags
            : tags.Count <= MaxTags ? tags
            : [.. tags.Take(MaxTags), new TagChip($"+{tags.Count - MaxTags}", string.Join("\n", tags.Skip(MaxTags).Select(t => t.Tip)))];
    }
    /// <summary>The shader mod by name, else plain ReShade; null = neither, or not looked (anti-cheat).</summary>
    internal static TagChip? ModChip(GameState s) => s.ShaderMod is { } mod
        ? new(mod, s.ShaderModBlocks ? "阻止该游戏进行编译" : s.ShaderModLayer ? "通过您的模组完成编译" : "少数模组修改的着色器未预编译")
        : s.ReShade ? new("ReShade", "未更改任何管线") : null;
    public string Shaders => Fmt.N(s.ShaderCount);
    public string Pipelines => Fmt.N(ScsKiller.PlanPipelines(s));
    public string Cache => Fmt.Cache(s);
    public string? CacheTip => Fmt.CacheTip(s);
    /// <summary>The cache column's text style: an estimate in the secondary colour.</summary>
    public string CacheStyle => CacheTip != null ? "Secondary" : "BodyTextBlockStyle";
    public string Time => Format.Duration(Fmt.CompileTime(s));

    bool Partly => ScsKiller.IsPartlyWarmed(s);
    bool PartlyCompiled => ScsKiller.IsPartlyCompiled(s);
    public string StatusText => s.Status switch
    {
        _ when s.CompileUnreached => "编译未改善",
        _ when PartlyCompiled => "部分编译",
        _ when ScsKiller.NotReached(s.StatusReason) => "尚未到达",
        GameStatus.Warmed when Partly => "部分预热",
        GameStatus.Warmed when s.Plan?.RecordedOnly == true => "已预热（仅录制管线）",
        GameStatus.Warmed => "已预热",
        GameStatus.Ready => "待编译",
        _ when ScsKiller.NeedsOfflineSession(s) => "需要离线会话",
        _ when ScsKiller.NeedsSteamStart(s) => "需先从 Steam 启动一次",
        GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => ScsKiller.NothingRecordedTitle,
        GameStatus.NeedsRecording => ScsKiller.RecordedEnough(s) ? "需要录制" : "需要录制 5 分钟",
        GameStatus.Stale => "需要重新构建",
        _ when s.StatusReason == ScsKiller.CheckingReason => "检查中…",
        _ when s.StatusReason == ScsKiller.NotCheckedReason => "未检查",
        _ when s.StatusReason == ScsKiller.CantReachReason => "不兼容",
        _ when s.StatusReason == SteamShortcutSource.NotReadyReason => "需要 Proton",
        _ => s.ShaderModBlocks ? "未编译" : s.Engine?.Encrypted == true ? "游戏文件已加密" : s.NotPlanned ? "不受支持" : "暂不支持",
    };
    /// <summary>The whole reason: the row's tooltip and the game page's status text.</summary>
    public string FullNote => s.Status switch
    {
        _ when s.CompileUnreached || PartlyCompiled || ScsKiller.NotReached(s.StatusReason) || ScsKiller.LinkedAway(s.StatusReason) => s.StatusReason,
        GameStatus.Warmed when Partly => $"driver {s.WarmedDriverVersion} · {s.Careful!.LaunchCompiled * 100:0}% still compiled at its first launch",
        GameStatus.Warmed => $"driver {s.WarmedDriverVersion}" + (ScsKiller.IsPartial(s.Plan) ? " · a recording compiles the rest"
            : ScsKiller.RtAfterRecording(s) ? " · " + ScsKiller.RtAfterRecordingNote : "") + Format.KeyNote(s),
        _ when ScsKiller.NeedsOfflineSession(s) || ScsKiller.NeedsSteamStart(s) => s.StatusReason,
        GameStatus.NeedsRecording when s.AnyAntiCheat && !s.RiskEligible => $"{ScsKiller.AntiCheatName(s)} blocks recording",
        GameStatus.NeedsRecording when s.RecordingPaused => ScsKiller.PausedNote(Host.Core.Settings),
        GameStatus.NeedsRecording when ScsKiller.NeverRecorded(s) => s.RecorderRefused!,
        GameStatus.NeedsRecording when s.RecorderInstalled && !ScsKiller.RecordedEnough(s) => Format.RecorderOnNote(s),
        _ => s.StatusReason,
    };
    /// <summary>The row's note under the status: a few words (<see cref="Format.ShortNote"/>); null when the status says it all.</summary>
    public string? Note => Format.ShortNote(s);
    // a mod the compile runs through takes the note's place; the tooltip keeps the full reason
    string? ListNote => s.ShaderModLayer ? $"{(s.Status == GameStatus.Warmed ? "已通过" : "将通过")} {s.ShaderMod} 协同编译" : Note;
    public string RowNote => Playing ? "正在游玩" + (ListNote is { } n ? " · " + n : "") : fold ?? ListNote ?? "";
    string? fold;
    /// <summary>A fold's share moved: only a row whose line changed updates.</summary>
    public void ShowFold() { if (Fmt.FoldLine(Id) is var f && f != fold) { fold = f; Changed(); } }
    public bool HasRowNote => RowNote.Length > 0;
    public string RowTip => (Playing ? "Playing now · " : "") + FullNote;
    /// <summary>The status text's style: StatusAccent, StatusPrimary, StatusWarn or StatusMuted.</summary>
    public string StatusStyle => s.Status switch
    {
        _ when s.CompileUnreached || PartlyCompiled || ScsKiller.NotReached(s.StatusReason) || ScsKiller.LinkedAway(s.StatusReason) => "StatusWarn",
        GameStatus.Warmed when Partly => "StatusWarn",
        GameStatus.Warmed => "StatusAccent",
        GameStatus.Ready => "StatusPrimary",
        GameStatus.Unsupported => "StatusMuted",
        _ => "StatusWarn",
    };

    // One action per status (Main.dc.html): Details / Add to queue / Record / disabled Why?
    public bool Queued => queued;
    public bool InAddAllReady => ScsKiller.InAddAllReady(s);
    public bool IsAdd => s.Status is GameStatus.Ready or GameStatus.Stale && !s.CompileUnreached;
    public string AddText => queued ? "排队中" : "加入队列";
    public bool CanAdd => !queued;
    public bool IsRecord => s.Status == GameStatus.NeedsRecording && !s.RecorderInstalled && s.NoStutter == null && !ScsKiller.NeedsOfflineSession(s) && !ScsKiller.NeedsSteamStart(s) && !s.RiskEligible && !Shared;
    bool Shared => Collections.RecordedBy(s.Game.Id) != null;   // recorded by its collection's first game's recorder, switched on that game's page
    public bool CanRecord => !s.AnyAntiCheat;
    public bool IsDetails => s.Status == GameStatus.Warmed || ScsKiller.IsUnchecked(s) || s.CompileUnreached || ScsKiller.NeedsOfflineSession(s) || ScsKiller.NeedsSteamStart(s) || (s.Status == GameStatus.NeedsRecording && (s.RecorderInstalled || s.NoStutter != null || s.RiskEligible || Shared));
    public bool IsWhy => s.Status == GameStatus.Unsupported && !ScsKiller.IsUnchecked(s);
    public string Reason => s.StatusReason;

    // Play goes through the store (a game the user added: its exe); never for an anti-cheat game (SCSKiller never launches them)
    public bool CanLaunch { get; private set; } = BeforeAfterUi.Launchable(s);
    public bool Compiling => compiling;
    public bool CanPlay => !Playing && !compiling;
    public string PlayText => Playing ? "运行中" : "开始游玩";
    public string PlayTip => Playing ? $"{Name} 正在运行" : compiling ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);

    public override string ToString() => $"{Name}, {StatusText}, {RowTip}";   // list item name for screen readers

    /// <summary>The game's latest state; no change raised when it is the one shown. A re-evaluated state is unequal even
    /// with nothing new (its lists are other instances): the bindings then set the same values again, which shows nothing.</summary>
    public void Update(GameState now, bool nowQueued, bool nowCompiling)
    {
        var icon = Host.Icons.Get(now.Game.ExePath);
        if (now == s && nowQueued == queued && nowCompiling == compiling && icon == Icon) return;
        (s, queued, compiling, Icon) = (now, nowQueued, nowCompiling, icon);
        CanLaunch = BeforeAfterUi.Launchable(s);
        // a new list makes the row build its chips again
        if (TagsOf(s) is var tags && !tags.SequenceEqual(Tags)) Tags = tags;
        Changed();
    }
}

/// <summary>One store's section of the library list (the rows shown after the search filter).</summary>
public sealed class StoreGroup(string store) : ObservableCollection<GameRow>
{
    public const string Recommended = "推荐编译";
    public string Store => store;
    public string CountText => $"{Count} game{(Count == 1 ? "" : "s")}" + (store == Recommended ? RecommendedNote() : "");
    // a row updated in place (compiled, cleared) changes the count of compiled ones without a collection change
    public void Recount() => OnPropertyChanged(new PropertyChangedEventArgs(nameof(CountText)));
    string RecommendedNote() => this.Count(r => r.State.Status == GameStatus.Warmed) is var done and > 0
        ? $" · stutters without a compile · {done} compiled" : " · stutters without a compile";
}

public sealed record HiddenRow(string Id, string Name, string Store)
{
    public string ShowName => $"Show {Name} in library";
}

public sealed class LibraryVm : Bindable
{
    static readonly LibraryScan firstScan = new();
    readonly BackgroundRead<(CacheUsage Usage, CacheLimit? Limit, long? Capped, long OldDriver, long? Fullest)> cache;

    public LibraryVm() => cache = new("Library cache", () => (Host.Core.Vendor.GetCacheUsage(), Host.Core.Vendor.GetCacheLimit(), (Host.Core.Vendor as AmdBackend)?.AppCache.DxcBytes(),
        Host.Core.OldDriverBytes(), Fullest()), ShowCache);

    /// <summary>A Linux driver's limit and Intel's are each game's own: the fullest game's cache; null for the other drivers.</summary>
    internal static long? Fullest() => Host.Core.Vendor.AppCache switch
    {
        NvidiaLinuxAppCache n => n.Fullest(), MesaLinuxAppCache m => m.Fullest(), IntelAppCache i => i.Fullest(), _ => null,
    };

    public IReadOnlyList<GameRow> Games { get; private set; } = [];   // every game, sorted; the list shows Groups
    /// <summary>The list: rows matching <see cref="Filter"/> per store, empty stores left out. Filtering reuses the rows
    /// (no icon reads, no IO).</summary>
    public ObservableCollection<StoreGroup> Groups { get; } = [];
    string filter = "";
    public string Filter { get => filter; set { if (value.Trim() != filter) { filter = value.Trim(); ApplyFilter(); Changed(); } } }
    LibraryShow show;
    public static readonly string[] ShowLabels = ["全部游戏", "暂不支持", "待编译", "已编译", "由 SCSKiller 编译"];
    public int ShowIndex { get => (int)show; set { if (value >= 0 && (LibraryShow)value != show) { show = (LibraryShow)value; ApplyFilter(); Changed(); } } }
    int shown;
    public bool Filtering => filter.Length > 0 || show != LibraryShow.All;
    public bool Searching => filter.Length > 0;
    public string ShownText => $"已显示 {shown} / {Games.Count} 款";
    public bool NoMatch => Filtering && Games.Count > 0 && shown == 0;
    public string NoMatchText => Searching ? $"未找到匹配“{filter}”的游戏" : "此状态下暂无游戏";
    public string SearchHint => Host.Platform.Stores.Count > 1 ? "Try part of the name, a store (Steam, GOG…) or an engine (UE 5)." : "尝试输入游戏名称片段或引擎（如 UE 5）。";
    /// <summary>A scan found no games at all, on an OS whose library says why.</summary>
    public bool NoGames => scanned && !Scanning && Games.Count == 0 && Host.Platform.NoGamesNote != null;
    public string NoGamesNote => NoGames ? Host.Platform.NoGamesNote!() : "";
    public bool CanAddGames => Host.Platform.AddGames;
    public string AddGamesNote => Host.Platform.AddGamesNote ?? "";
    public bool HasAddGamesNote => Host.Platform.AddGamesNote != null;
    /// <summary>The games the user removed from the library, listed while <see cref="ShowHidden"/> is on.</summary>
    public IReadOnlyList<HiddenRow> Hidden { get; private set; } = [];
    public bool HasHidden => Hidden.Count > 0;
    bool showHidden;
    public bool ShowHidden { get => showHidden; set { if (value != showHidden) { showHidden = value; Changed(); } } }
    public bool HiddenShown => showHidden && HasHidden;
    public async void ShowInLibrary(HiddenRow r)
    {
        try { await Task.Run(() => Host.Core.ShowGame(r.Id)); }
        catch (ArgumentException) { }   // shown again meanwhile
        Refresh();
    }

    LibrarySort sort = Host.Store.LoadLibrarySort();
    /// <summary>A column header's click: its first order, then the reverse. Status goes back to the default order.</summary>
    public void SortBy(LibrarySort first, LibrarySort second)
    {
        sort = sort == first ? second : first;
        try { Host.Store.SaveLibrarySort(sort); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // sorted all the same, just not remembered
        Refresh();
    }
    string Arrow(LibrarySort up, LibrarySort down) => sort == up ? " ↑" : sort == down ? " ↓" : "";
    public string GameHeader => "游戏" + Arrow(LibrarySort.NameAscending, LibrarySort.NameDescending);
    public string PipelinesHeader => "管线" + Arrow(LibrarySort.PipelinesAscending, LibrarySort.PipelinesDescending);
    public string CacheHeader => "缓存" + Arrow(LibrarySort.CacheAscending, LibrarySort.CacheDescending);
    public bool Scanning { get; private set; }
    int refreshing;   // Rescan calls not finished: the scan, then a user refresh's server fetches
    public bool Refreshing => refreshing > 0;
    string? refreshNote;
    public string? RefreshNote => (ScanEmpty ? null : Fmt.AfterUpdateLine) ?? refreshNote;
    public bool HasRefreshNote => RefreshNote != null;
    public string? Error { get; private set; }
    public bool HasError => Error != null;
    public string? GpuNotice => Format.GpuNotice(Host.Core.Vendor.Gpu, Host.Core.Settings.GpuNoticeDismissed,
        Host.Core.Vendor is IntelBackend || MesaLinux.Is(Host.Core.Vendor.Caps), (Host.Core.Vendor as UnsupportedVendor)?.Why);
    public bool HasGpuNotice => GpuNotice != null;
    public void DismissGpuNotice()
    {
        Host.Core.Settings = Host.Core.Settings with { GpuNoticeDismissed = Host.Core.Vendor.Gpu.Name };
        Changed();
    }

    public string Summary { get; private set; } = "";
    public int ReadyCount { get; private set; }     // ready, not queued yet and not a no-stutter game
    public int WaitingCount { get; private set; }
    public string AddAllText => $"全部待编译 ({ReadyCount})";
    public bool CanAddAll => ReadyCount > 0 && !Scanning;
    public int RecommendedCount { get; private set; }   // stutters without a compile, ready and not queued yet
    public string AddRecommendedText => $"全部推荐 ({RecommendedCount})";
    public bool CanAddRecommended => RecommendedCount > 0 && !Scanning;
    public bool CannotAddRecommended => !CanAddRecommended;   // the plain, disabled twin of the accent button (theme brushes stay in XAML)
    public string CompileQueueText => $"编译队列 ({WaitingCount})";
    public bool CanCompileQueue => WaitingCount > 0;

    public string CacheLine { get; private set; } = "";
    public string? CacheUpperNote { get; private set; }
    public bool CacheWarn => CacheWarnText != null;
    public string? CacheWarnText { get; private set; }
    public bool CanRaiseLimit => Host.Core.Vendor.Caps.CacheSizeConfigurable;
    /// <summary>Below this, what an older NVIDIA driver left (old-type files of desktop apps, a few MB) isn't worth a line.</summary>
    const long OldDriverShown = 1L << 30;
    public string? OldDriverNote { get; private set; }
    public bool HasOldDriver => OldDriverNote != null;

    public string Threads => ScsKiller.UsedThreads(Host.Core.Settings.Threads).ToString();
    public string ThreadsOf => $"共 {Environment.ProcessorCount} 线程";

    public void Load()
    {
        Refresh();
        if (firstScan.Start()) Rescan(force: false);
        else scanned = true;   // ran from an earlier load, or runs now: Scanning says which
    }

    // The scan message waits 300 ms so a cached scan doesn't flicker it; the list fills when the scan ends.
    bool slowScan, forced, scanned;
    public bool ScanEmpty => slowScan && Games.Count == 0;
    public bool ScanBusy => slowScan && Games.Count > 0;
    public string ScanEmptyNote => Fmt.AfterUpdateLine ?? $"正在检查 {Stores(Host.Platform.Stores)}，随后识别各游戏所用引擎。首次扫描需要读取游戏文件，可能需要一到两分钟。";
    static string Stores(IReadOnlyList<string> s) => s.Count > 1 ? $"{string.Join(", ", s.Take(s.Count - 1))} and {s[^1]}" : string.Join("", s);
    public string ScanTitle => Fmt.ScanProgress ?? "正在检查发生变动的游戏…";
    public string ScanEmptyTitle => Fmt.ScanProgress ?? "正在查找您的游戏…";
    /// <summary>A scan's step: the last list, which the first scan puts up before it reads the games, else the progress line.</summary>
    public void ScanStep()
    {
        if (Games.Count == 0 && Host.Core.Games.Count > 0) Refresh();
        else Changed();
        foreach (var r in Games) r.ShowFold();
    }
    public string ScanBusyNote => forced ? "正在重新读取每款游戏的引擎与反作弊信息。每款游戏在检查完毕后会实时更新状态。"
        : "每款游戏在检查完毕后会实时更新状态。";

    public async void Rescan(bool force = true, bool userRequested = false)
    {
        Scanning = true; refreshing++; forced = force; Error = refreshNote = null; Refresh();   // the summary says "Looking for games…" while nothing is listed
        var scan = force ? Host.Core.RescanAsync(CancellationToken.None, userRequested) : Host.Core.ScanAsync(CancellationToken.None, userRequested);
        if (await Task.WhenAny(scan, Task.Delay(300)) != scan) { slowScan = true; Changed(); }
        try { await scan; firstScan.Ended(ok: true); }
        catch (Exception e) { Error = "扫描失败: " + e.Message; firstScan.Ended(ok: false); }
        Scanning = slowScan = false;
        scanned = true;
        Refresh();
        if (userRequested && Error == null && Host.Core is ScsKiller k)
            refreshNote = await k.ServerRefresh switch
            {
                ScsKiller.ServerCheck.TooSoon => "几分钟前刚刚检查过服务器。",
                ScsKiller.ServerCheck.Unreachable => "无法连接到 SCSKiller 服务器。列表保持原状。",
                _ => null,
            };
        refreshing--;
        Changed();
    }

    public void Refresh()
    {
        var core = Host.Core;
        var list = Library.Sorted(core.Games, sort).ToList();
        var queue = core.Queue.Where(q => !q.PlanCheck).ToList();
        var queued = queue.Where(Fmt.Active).Select(q => q.GameId).ToHashSet();
        var rows = Games.ToDictionary(r => r.Id);
        Games = [.. list.Select(g =>
        {
            bool q = queued.Contains(g.Game.Id), compiling = Fmt.CompilingSoon(queue, g.Game.Id);
            if (!rows.TryGetValue(g.Game.Id, out var row)) return new GameRow(g, q, compiling);
            row.Update(g, q, compiling);
            return row;
        })];
        foreach (var shown in Games) shown.ShowFold();
        ApplyFilter();
        Hidden = [.. core.HiddenGames.Select(g => new HiddenRow(g.Id, g.Name, Fmt.StoreName(g)))];

        int ready = list.Count(g => g.Status is GameStatus.Ready or GameStatus.Stale);
        ReadyCount = Games.Count(r => r.InAddAllReady && !r.Queued);
        RecommendedCount = RecommendedToAdd().Count;
        WaitingCount = queue.Count(q => q.Stage == QueueStage.Waiting);
        var storeText = Fmt.FoundIn(Games.Select(r => r.StoreName));
        bool allUnreal = list.Count > 0 && list.All(g => g.Engine?.Family == "Unreal");
        Summary = Scanning && list.Count == 0 ? "正在查找您的游戏…"
            : $"共发现 {list.Count} 款{(allUnreal ? "虚幻引擎 (Unreal Engine) " : "")}游戏{(storeText.Length > 0 ? "（来自 " + storeText + "）" : "")} · {ready} 款可供编译";

        cache.Request();
        Changed();
    }

    /// <summary>Games -> Groups: same order within a store (<see cref="SortBy"/>). Sections and rows are kept and only moved,
    /// added or removed (<see cref="ListSync"/>): a Reset scrolls the list to the top and builds every row again.</summary>
    void ApplyFilter()
    {
        var visible = Games.Where(r => (filter.Length == 0 || r.Matches(filter)) && Library.Shows(r.State, show)).ToList();
        // known-stutter games go on top (StutterList.Recommended), out of their store's section, compiled or not
        var byId = visible.ToDictionary(r => r.Id);
        var recommended = StutterList.Recommended(visible.Select(r => r.State)).Select(s => byId[s.Game.Id]).ToList();
        if (sort != LibrarySort.Status) recommended = visible.Intersect(recommended).ToList();   // in the column's order
        var groups = visible.Except(recommended).GroupBy(r => r.StoreName)
            .OrderBy(g => Array.IndexOf(Fmt.Stores, g.Key)).Select(g => (g.Key, Rows: g.ToList())).ToList();
        if (recommended.Count > 0) groups.Insert(0, (StoreGroup.Recommended, recommended));
        shown = groups.Sum(g => g.Rows.Count);
        var wanted = groups.ToDictionary(g => g.Key, g => g.Rows.ToHashSet());
        // a row leaves its old section before it joins its new one: in two sections at once it would get two containers
        foreach (var have in Groups)
            for (int i = have.Count - 1; i >= 0; i--)
                if (!wanted.TryGetValue(have.Store, out var keep) || !keep.Contains(have[i])) have.RemoveAt(i);
        var kept = Groups.ToDictionary(g => g.Store);
        ListSync.Sync(Groups, [.. groups.Select(g => kept.GetValueOrDefault(g.Key) ?? new StoreGroup(g.Key))]);
        foreach (var (group, (_, rows)) in Groups.Zip(groups))
        {
            ListSync.Sync(group, rows);
            group.Recount();
        }
    }

    // Capped: AMD's limit is of its DirectX 12 cache alone, which the usage counts with the DirectX 11 one. Fullest: a Linux
    // driver's limit and Intel's are each game's own, so the fullest game is the one that nears it
    void ShowCache((CacheUsage Usage, CacheLimit? Limit, long? Capped, long OldDriver, long? Fullest) c)
    {
        var (usage, limit, capped, old, _) = c;
        var fullest = limit?.Bytes != null ? c.Fullest : null;   // with no limit the header shows the whole cache
        OldDriverNote = old >= OldDriverShown ? $"{Format.Bytes(old)} 旧驱动残留文件（当前驱动不再使用）" : null;
        CacheLine = fullest is { } f && limit?.Bytes is { } lb ? $"{Format.Bytes(f)} of {Format.Bytes(lb)} in the fullest game"
            : Format.CacheSplit(usage, limit, capped, Host.Core.WindowsCacheBytes);
        CacheUpperNote = Format.CacheUpperNote(usage);
        CacheWarnText = Format.CacheWarning(Host.Core.Vendor.Vendor, limit, fullest ?? capped ?? usage.BytesOnDisk, fullest != null);
        Changed();
    }

    /// <summary>The Recommended section's games that can be queued now (not the ones that need a recording), every row, not the filtered view.</summary>
    List<GameRow> RecommendedToAdd()
    {
        var byId = Games.ToDictionary(r => r.Id);
        return StutterList.Recommended(Games.Select(r => r.State)).Select(s => byId[s.Game.Id]).Where(r => r.IsAdd && !r.Queued).ToList();
    }

    public void AddAllRecommended()
    {
        foreach (var g in RecommendedToAdd()) Host.Core.Enqueue(g.Id);
        Refresh();
    }

    public async void Play(GameRow row)
    {
        Error = await BeforeAfterUi.PlayAsync(row.State);
        Changed();
    }

    public void AddAllReady()
    {
        foreach (var g in Games.Where(r => r.InAddAllReady && !r.Queued).ToList()) Host.Core.Enqueue(g.Id);
        Refresh();
    }
}
