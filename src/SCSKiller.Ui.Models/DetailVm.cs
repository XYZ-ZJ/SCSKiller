using System.Globalization;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Ui.Models;

/// <summary>The status card's icon, which the UI draws from its icon font.</summary>
public enum StatusIcon { Warning, Warmed, Ready, Record, Blocked }

public sealed class DetailVm(string id) : Bindable
{
    GameState s = Host.Core.Games.First(g => g.Game.Id == id);
    public GameRow Row { get; private set; } = new(Host.Core.Games.First(g => g.Game.Id == id));
    public string? Error { get; set; }
    public bool HasError => Error != null;

    public void Refresh()
    {
        place = InQueue(id);
        var now = Host.Core.Games.FirstOrDefault(g => g.Game.Id == id);
        if (now != null && s.Playing && !now.Playing) ReadCaches();   // the game exited: its Windows and pipeline caches grew
        if (now != null && (now != s || Row.Icon != Host.Icons.Get(now.Game.ExePath))) { s = now; Row = new GameRow(now); }   // or its icon failed: the tile
        ReadBlocked();
        ReadLaunchLine();
        ReadSteamCacheNote();
        Changed();
    }

    static string Sentence(string t) => t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;
    bool Partial => ScsKiller.IsPartial(s.Plan);
    bool KeyMissing => s.Engine?.KeyMissing == true;
    /// <summary>Needs a recording for its ray tracing only: the rest compiles (a partial compile, offered but not the primary action).</summary>
    bool RtPartial => s.Status == GameStatus.NeedsRecording && ScsKiller.NeedsRtRecording(s);
    /// <summary>Compiled, and a recording would add its ray tracing: the record tip, not the warning callout.</summary>
    bool RtAfter => ScsKiller.RtAfterRecording(s);
    public bool NoAntiCheat => !s.AnyAntiCheat;
    /// <summary>The recorder may go next to the game: no anti-cheat, not a game added by hand whose folder isn't confirmed, and
    /// not one whose first d3d12.dll Streamline loads on NVIDIA.</summary>
    bool CanRecord => NoAntiCheat && !s.RootUnconfirmed && !s.StreamlineFirst;

    public string Name => s.Game.Name;
    public string Sub => $"{Fmt.FileName(s.Game.ExePath)} · {Fmt.StoreName(s.Game)}";   // engine and API are tags

    // Status: the state in its colour and why; the actions sit beside it.
    public StatusIcon StatusIcon => s.Status switch
    {
        _ when s.CompileUnreached || ScsKiller.IsPartlyCompiled(s) || ScsKiller.NeedsSteamStart(s) => StatusIcon.Warning,
        GameStatus.Warmed => StatusIcon.Warmed, GameStatus.Ready => StatusIcon.Ready, GameStatus.NeedsRecording => StatusIcon.Record,
        GameStatus.Stale => StatusIcon.Warning, _ => StatusIcon.Blocked,
    };
    // The queue page's progress for this game: its number in the run order while it waits, its stage and progress while it runs.
    (QueueItem? Item, int Number) place = InQueue(id);
    static (QueueItem?, int) InQueue(string id)
    {
        var (running, waiting) = Fmt.RunOrder(Host.Core.Queue);
        if (running?.GameId == id) return (running, 1);
        int i = waiting.FindIndex(q => q.GameId == id);
        return i < 0 ? (null, 0) : (waiting[i], i + (running != null ? 2 : 1));
    }
    /// <summary>A warm's tick: only the progress changes, unless the item started, finished or left the queue.</summary>
    public void RefreshProgress()
    {
        var was = place.Item?.Stage;
        place = InQueue(id);
        if (place.Item?.Stage != was) Refresh();
        else Changed(nameof(StatusTitle), nameof(StatusReason), nameof(Percent), nameof(Indeterminate));
    }
    QueueItem? Running => place.Item is { } q && Format.Running(q) ? q : null;
    public bool IsCompiling => Running != null;
    public double Percent => Fmt.Percent(Running);
    public bool Indeterminate => Fmt.Indeterminate(Running);

    public string StatusTitle => Running is { } r ? Fmt.Stage(r) : place.Item != null ? $"排队中（第 {place.Number} 位）" : Row.StatusText;
    public string StatusReason => Running is { } r && (Fmt.ProgressText(r) is { } p
            ? p + (r.Progress is { PerSecond: > 0 } && Fmt.Left(r, s) is { } t ? $" · 剩余约 {Format.Duration(t)}" : "") + (Fmt.Started(r) is { } at ? " · " + at : "")
            : Fmt.Started(r)) is { } line
        ? Sentence(line)
        : place.Item is { Stage: QueueStage.Waiting, Note: { } n } ? Sentence(n)
        : Fmt.FoldLine(id) ?? GameReason;
    string GameReason => Sentence(s.Status switch
    {
        _ when ScsKiller.CacheOffRefused(Host.Core.Queue, id) => ScsKiller.CacheOffReason,
        _ when s.CompileUnreached => s.StatusReason + (s.WarmedAt != null ? ". " + Sentence(ScsKiller.ClearForGameNote) : ""),
        _ when ScsKiller.IsPartlyCompiled(s) => ScsKiller.RefusedNote(s) + "。后续 SCSKiller 更新可能会修复此问题",
        GameStatus.Warmed when ScsKiller.IsPartlyWarmed(s) => s.StatusReason,
        GameStatus.Warmed => (Partial || RtAfter || KeyMissing ? "" : "着色器缓存完整，") + $"已针对 {(ScsKiller.BothApis(s.Plan) ? "DirectX 11 和 DirectX 12，" : "")}驱动版本 {s.WarmedDriverVersion} 完成编译" + (s.WarmedAt is { } t ? $"，编译时间：{t.LocalDateTime:g}" : "")
            + (RtAfter ? ". " + Sentence(ScsKiller.RtAfterRecordingNote) : "") + (KeyMissing ? ". " + Sentence(ScsKiller.KeyForFullCoverage) : ""),
        // a cache here: played since a clear or a stopped compile; then the planner's note when the row has one to say
        GameStatus.Ready => (s.CacheOnDisk > 0 ? $"尚未完全编译：驱动缓存中已有 {Format.Bytes(s.CacheOnDisk)}"
            : s.LastWarmTime == null ? "尚未编译" : "着色器缓存为空：请重新编译")
            + (Row.Note != null ? ". " + Sentence(s.StatusReason) : ""),
        GameStatus.NeedsRecording => HasDbTeaser ? NoDbNote(Row.FullNote) : Row.FullNote,   // the teaser says it
        _ when s.Engine?.Encrypted == true => "需要提供游戏的 AES 密钥",   // the title: "Encrypted game files"
        _ => HasDbTeaser ? NoDbNote(s.StatusReason) : s.StatusReason,
    })
        + (IsPartlyCompiled || s.CompileUnreached ? "." : "") + Format.ModNote(s);
    static string NoDbNote(string r) => r.Replace(". " + Sentence(ScsKiller.InDbSupportersNote), "").Replace(". " + Sentence(ScsKiller.InDbNote), "");
    public bool IsPartlyCompiled => ScsKiller.IsPartlyCompiled(s);
    public bool NeedsAesKey => ScsKiller.NeedsAesKey(s);
    public bool HasAesKey => Host.Core.HasEncryptionKey(id);
    public bool CanChangeAesKey => HasAesKey && !NeedsAesKey;
    public bool NeedsGameKey => ScsKiller.NeedsGameKey(s);
    public const string RemoveAesKeyNote = "AES 密钥已从此电脑删除。已编译的缓存仍将保留。下次编译时将不会包含需要此密钥解密的着色器，若游戏 exe 中包含该密钥，SCSKiller 将再次自动读取。";
    public string PartlyCompiledWhy => s.LastWarmFailedBy switch
    {
        0 or 1 => $"SCSKiller 已编译此游戏，但显卡驱动拒绝了其中很大一部分管线。这通常发生在显卡驱动或 {Host.Platform.Runtime} 无法以游戏运行时的特定方式构建管线时。被拒绝的管线将在游戏首次需要时由游戏自行构建，届时可能会引起卡顿。显卡驱动更新后 SCSKiller 会重新尝试编译它们。",
        2 => "SCSKiller 编译了此游戏，但无法从游戏文件中构建其中很大一部分管线。游戏首次遇到它们时会自行构建，届时可能会出现卡顿。",
        _ => "SCSKiller 编译了此游戏，但其中很大一部分管线无法完成编译。游戏首次遇到它们时会自行构建，届时可能会出现卡顿。显卡驱动更新后 SCSKiller 会重新尝试编译。",
    };

    // The manifest is public: a PC without "db" sees that the community database covers the game, and where to get it.
    // Shown, it is the page's only mention of the database.
    public bool HasDbTeaser => s is { InCommunityDb: true, Community: null } && Host.Account?.HasDb != true
        && (s.Status == GameStatus.NeedsRecording || RtAfter || s.Status == GameStatus.Unsupported && !NoAntiCheat && s.StatusReason.StartsWith("needs a recording, which", StringComparison.Ordinal));
    public string DbTeaser => $"社区数据库中已有其他玩家录制的 {s.CommunityDbPsos:N0} 个管线。Patreon 赞助者无需录制即可直接编译。";
    public string DbTeaserLink => Host.Account?.SignedIn == true ? "Patreon 会员权益" : "使用 Patreon 登录";
    public bool CanCompile => s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale || RtPartial;
    public bool ShowMaxCompile => CanCompile && ScsKiller.MaxCompileAdds(Host.Core.Vendor.Caps, s.Engine);
    public const string MaxCompileWhy = "编译该游戏着色器的每一种组合，而不仅仅是每种编译一次。耗时更长且占用更多磁盘空间，但能尽可能减少游玩时需要临时编译的着色器数量。在 AMD 显卡上效果最显著。";
    public string? GuessNote => s.Engine is { } e && s.Status is GameStatus.Ready or GameStatus.Warmed or GameStatus.Stale ? ScsKiller.GuessNote(e) : null;
    public bool HasGuessNote => GuessNote != null;
    public string? DeliveryLine => ScsKiller.DeliveryLine(s);
    public bool HasDeliveryLine => DeliveryLine != null;
    // read live: a game's state keeps the flag until its next refresh
    public string? CleanupLine => D3DSCache.CleanupKept() == true ? null : ScsKiller.CleanupLine(s);
    public bool HasCleanupLine => CleanupLine != null;
    public bool CanKeepShaders { get; private set; } = true;
    public async Task KeepShadersAsync()
    {
        CanKeepShaders = false;
        Changed();
        var r = await SettingsVm.KeepShadersAsync(true);
        CanKeepShaders = true;
        if (r is { Ok: false }) { Error = "未更改：" + r.Message; Changed(); }
        else RefreshGame();
    }
    public string? VulkanRtNote => ScsKiller.VulkanRtNote(s, GpuBackends.OwnDriver(Host.Core.Vendor));
    public bool HasVulkanRtNote => VulkanRtNote != null;
    // warmed: nothing urgent, unless only partly (the careful compile is offered); RT: Record comes first; no shader stutter: not needed
    public bool CompileAccent => (OffersCareful || s.Status != GameStatus.Warmed && !RtPartial) && s.NoStutter == null;
    bool Queued => Host.Core.Queue.Any(q => q.GameId == id && Fmt.Active(q) && !q.PlanCheck);
    public bool CanAdd => !Queued && !BaInProgressAny;
    /// <summary>A pair before Done: a compile or clear of the user's own would spoil a run (Core refuses them too).</summary>
    bool BaInProgressAny => s.BeforeAfter is { Stage: not BeforeAfterStage.Done };
    const string BaBusyTip = "对比测试录制正在进行中";
    public string? CompileTip => BaInProgressAny ? BaBusyTip : null;
    public bool CanClearCache => HasCache && !BaInProgressAny;
    bool CompilingSoon => Fmt.CompilingSoon(Host.Core.Queue, id);
    public bool ShowPlay => Row.CanLaunch;
    public bool CanPlay => !s.Playing && !CompilingSoon;
    public string PlayText => s.Playing ? "运行中" : "开始游玩";
    public string PlayTip => s.Playing ? $"{s.Game.Name} 正在运行" : CompilingSoon ? Fmt.CompilingTip : Fmt.PlayVia(s.Game);
    public bool IsManual => s.Game.Store == Store.Manual;
    public bool CanChooseExe => !IsManual && Collections.Member(s.Game.Id) == null;
    public bool ExeChosen => CanChooseExe && s.ExeChosen;
    public string RemoveTip => IsManual ? "移除此游戏。若安装了录制器将从游戏目录清理，游戏原有文件保持不变。"
        : "隐藏此游戏。其文件、存档及已编译缓存均会保留。";
    /// <summary>The confirmation's text for a store's game, which is hidden.</summary>
    public string HideNote => "隐藏后该游戏将不再显示在列表中，且不参与自动编译。其游戏文件、存档及已编译缓存均会保留。在游戏库页面勾选“显示已隐藏”可重新显示。"
        + (BaInProgressAny ? " 正在进行的对比测试录制将被取消。" : "")
        + (s.RecorderInstalled ? " 录制器已关闭并已从游戏文件夹中移除。" : "");
    public async void Play()
    {
        Error = await BeforeAfterUi.PlayAsync(s);
        Changed();
    }
    // Before and after recording (doc 6.5): one button in the status card, its note under it, a menu beside it once started
    // off the UI thread: it loads game records and lists processes. Until the first read is in, the button is off with no note
    string? blocked;
    bool blockedKnown;
    BackgroundRead<string?>? blockedRead;
    void ReadBlocked() => (blockedRead ??= new("game blocked", () => Blocked(id), b =>
    {
        if (blockedKnown && b == blocked) return;
        (blocked, blockedKnown) = (b, true);
        Changed();
    })).Request();
    static string? Blocked(string id)
    {
        if (!Host.Core.Settings.BeforeAfter) return null;
        try
        {
            var why = Host.Core.BeforeAfterBlocked(id);
            return why != null && why == Host.Core.Games.First(g => g.Game.Id == id).StatusReason ? Sentence(why) : why;   // a status reason starts lowercase
        }
        catch (Exception e) { return BeforeAfterUi.Describe(e); }
    }
    BeforeAfterInfo? Ba => s.BeforeAfter;
    (bool Shown, bool Enabled, string Note) BaStart => BeforeAfter.StartButton(Host.Core.Settings.BeforeAfter, blockedKnown ? blocked : "");
    public bool ShowBa => Ba != null ? Host.Core.Settings.BeforeAfter : BaStart.Shown;
    public bool BaHasMenu => ShowBa && Ba != null;
    public bool BaVoided => BeforeAfterUi.Voided(Ba);
    public bool BaInProgress => Ba is { Stage: not BeforeAfterStage.Done };   // voided too: Cancel stays
    public bool BaDone => Ba is { Stage: BeforeAfterStage.Done } && !BaVoided;
    bool BaLaunchable => BeforeAfterUi.Launchable(s);
    public string BaText => Ba switch
    {
        null => "编译前后对比测试",
        _ when BaVoided => "重新开始",
        { Stage: BeforeAfterStage.BeforeArmed, ClearPending: true } => "清除缓存并重试",
        { Stage: BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed } => BaLaunchable ? $"启动 {Name}" : $"照常启动 {Name}",
        { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } p => p.Minutes * 60 - p.PlayedSeconds is var left and > 0
            ? $"对比测试：正在游玩，剩余 {(int)left / 60}:{(int)left % 60:00}" : "对比测试：时间到，请退出游戏",
        { Stage: BeforeAfterStage.Compiling or BeforeAfterStage.AfterReady } => "继续进行编译后测试",
        _ => "查看对比报告",
    };
    public bool BaEnabled => Ba?.Stage switch
    {
        null => BaStart.Enabled,
        _ when BaVoided || Ba!.ClearPending => !s.Playing,
        BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed => BaLaunchable && !s.Playing,
        BeforeAfterStage.AfterReady => !s.Playing,
        BeforeAfterStage.Done => true,
        _ => false,
    };
    public bool BaAccent => BaVoided || Ba?.Stage is BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed or BeforeAfterStage.AfterReady;
    public string? BaTip => Ba == null ? blocked ?? "测试冷启动表现、进行编译、再次测试，并生成可分享的对比报告"
        : Ba.Stage is BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed && !BaLaunchable ? $"请照常启动 {Name}：SCSKiller 会自动检测到游戏启动" : null;
    public string BaNote => Ba switch
    {
        null => BaStart.Note,
        { Problem: { } problem } => problem,
        { Stage: BeforeAfterStage.BeforeArmed } b => $"从可重复加载的存档开始游玩 {b.Minutes} 分钟，随后关闭游戏",
        { Stage: BeforeAfterStage.AfterArmed } a => $"加载相同的存档并游玩相同的 {a.Minutes} 分钟",
        { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } => "时间结束后请关闭游戏",
        { Stage: BeforeAfterStage.Compiling } => "正在等待编译完成",
        { Stage: BeforeAfterStage.AfterReady } => "编译已完成：接下来进行编译后测试",
        _ => "",
    };
    public bool HasBaNote => ShowBa && BaNote.Length > 0;
    /// <summary>The compile failed or was stopped: the note says why.</summary>
    public bool BaCompileAgain => Ba is { Stage: BeforeAfterStage.Compiling, Problem: not null } && !BaVoided && !Queued;

    public string AddText => Queued ? "排队中" : OffersCareful ? "谨慎编译" : RtPartial ? "不含光线追踪编译（部分）" : "编译";
    /// <summary>Partly warmed by a fast compile: the compile button turns the careful compile on first.</summary>
    public bool OffersCareful => ScsKiller.IsPartlyWarmed(s) && s.Careful is { On: false };

    // AMD's careful compile: a switch in the compile card, off while the game is queued (a running compile keeps its schedule)
    public bool HasCareful => s.Careful != null && CanCompile;
    public bool? CarefulPending { get; set; }
    public bool LeftOutOn => LeftOutPending ?? s.LeftOutOfAddAll;
    public bool? LeftOutPending { get; set; }
    public bool SharedOn => SharedPending ?? s.SharedHashes;
    public bool? SharedPending { get; set; }
    public string SharedNote => s.SharedChoice == null ? "跟随全局设置" : "为此游戏单独设置";
    public bool ShowSharedFollow => SharedPending == null && s.SharedChoice != null;
    public bool CarefulOn => CarefulPending ?? s.Careful?.On == true;
    public bool CanToggleCareful => CarefulPending == null && !Queued;
    public string CarefulNote => "仅适用于编译失败的游戏。" + (s.Careful is { Recorded: 0 } ? "在少量线程上分批次编译游戏内录制的管线，以便驱动保留更多缓存。该游戏尚未录制，因此暂无可谨慎编译的内容。"
        : $"在 {ScsKiller.AmdCarefulThreads} 个线程上分批次编译游戏内录制的管线，以便驱动保留更多缓存。其余管线全速编译。"
          + (s.Careful?.Estimate is { } est ? $" About {Format.Duration(est)}" + (s.Careful.On || Fmt.CompileTime(s) is not { } fast ? "." : $" instead of {Format.Duration(fast)}.") : ""))
        + (s.Careful?.LaunchCompiled is { } lc ? $" 上次编译后，游戏首次启动时仍自行编译了 {lc * 100:0}% 的管线。" : "");
    // AMD: the compiled cache kept aside, in the compile card
    public bool HasKept => s.Kept != null && Host.Core.Vendor.Vendor == GpuVendor.Amd && Host.Core.Settings.KeepCachesAside;
    public string KeptSize => s.Kept is { } k ? Fmt.KeptSize(k) : "";
    public string KeptState => s.Kept switch
    {
        null => "",
        { Unkept: > 0 and var n, HeldBy: var who } => $"{n} 个文件正被 {who ?? "其他应用程序"} 占用，未被单独保留备份。",
        { Frees: > 0 } when s.AntiCheat != AntiCheat.None => "将在游戏启动后数秒内放回。请保持 SCSKiller 运行。",   // no Play button, VAC keeps it
        { Frees: > 0 } => "驱动移除了其中部分缓存。点击“开始游玩”时会自动放回。",
        { PutBackAt: { } at, Late: true } => $"已于游戏启动后的 {Fmt.When(at)} 放回。部分着色器可能已重新编译。",
        { PutBackAt: { } at } => $"已于游戏启动前的 {Fmt.When(at)} 放回。",
        _ => "目前位于显卡驱动缓存中。",
    };

    // 0 = the global setting, then NewShadersMode's values in order
    public static readonly string[] NewShadersLabels = ["关闭", "通知我", "自动编译并通知我", "静默自动编译"];
    public List<string> NewShadersChoices => Same(ref newShadersChoices, [$"跟随全局设置 ({NewShadersLabels[(int)NewShaders.Global(Host.Core.Settings)]})", .. NewShadersLabels]);
    // the same list while its labels hold: a new one resets the selection
    List<string>? newShadersChoices, rayTracingChoices, cacheCapChoices, traversalChoices;
    static List<string> Same(ref List<string>? kept, List<string> now) => kept != null && kept.SequenceEqual(now) ? kept : kept = now;
    public int NewShadersIndex => s.NewShadersOverride is { } m ? (int)m + 1 : 0;
    // 0 = the global setting, 1 = on, 2 = off
    public List<string> RayTracingChoices => Same(ref rayTracingChoices, [$"默认 ({(Host.Core.Settings.CompileRayTracing ? "开启" : "关闭")})", "开启", "关闭"]);
    public int RayTracingIndex => s.RayTracingOverride switch { null => 0, true => 1, false => 2 };
    public bool ShowTraversal => TraversalFix.Applies(s, Host.Core.Vendor.Caps);
    public List<string> TraversalChoices => Same(ref traversalChoices, TraversalFix.GameChoices(Host.Core.Settings, s));
    public int TraversalIndex => TraversalFix.GameIndex(s);
    public string TraversalNote => TraversalFix.GameNote(s, Host.Core.Vendor.Gpu);
    public bool TraversalLowVram => TraversalFix.LowVram(Host.Core.Vendor.Gpu) == true;
    public string TraversalWarning => TraversalFix.LowVramWarning;
    // no plan, or one from before inline ray tracing was counted: unknown, so the choice shows
    public bool ShowRayTracing => P?.RtInline == null || s.RayTracingOverride != null || ScsKiller.HasRayTracing(P);
    public List<string> CacheCapChoices => Same(ref cacheCapChoices, ["无限制", .. ScsKiller.CacheCapsGB.Select(gb => $"{gb} GB")]);
    public int CacheCapIndex => s.CacheCapGB is { } gb ? Array.IndexOf(ScsKiller.CacheCapsGB, gb) + 1 : 0;
    public bool ShowCacheCap => ScsKiller.HasCacheCap(s);
    // Windows' shader cache of the game, read off the UI thread: Clear cache clears it too, so it can be the only cache to clear.
    BackgroundRead<long>? otherCaches;
    long otherBytes;
    public void ReadCaches() => (otherCaches ??= new("game caches", OtherCacheBytes, b => { otherBytes = b; Changed(); })).Request();
    long OtherCacheBytes()
    {
        try { return Host.Core.GameCaches(id).Where(p => p.Name != ScsKiller.DriverPart).Sum(p => p.Bytes); }
        catch (Exception) { return 0; }   // e.g. the game is gone after a rescan
    }
    // read off the UI thread: it reads Steam's localconfig.vdf
    LaunchLine? launchLine;
    BackgroundRead<LaunchLine?>? launchRead;
    void ReadLaunchLine()
    {
        if (!Host.Platform.SteamLaunchLine) return;
        (launchRead ??= new("launch line", () => LaunchLineOf(id), l =>
        {
            if (l == launchLine) return;
            launchLine = l;
            Changed();
        })).Request();
    }
    static LaunchLine? LaunchLineOf(string id)
    {
        try { return Host.Core.LaunchLineOf(id); }
        catch (Exception) { return null; }   // e.g. on Mesa, a game outside the Steam libraries
    }
    public bool HasLaunchLine => launchLine != null;
    public bool OffersLaunchLine => launchLine is { Refused: null };
    public string LaunchLineText => launchLine?.Text ?? "";
    public bool LaunchLineSet => launchLine?.Set == true;
    public bool CanCopyLaunchLine => launchLine is { Set: false, Refused: null };
    public string? LaunchLineRefused => launchLine?.Refused is { } why ? $"无须粘贴启动选项：{why}。" : null;
    public bool HasLaunchLineRefused => LaunchLineRefused != null;
    bool launchCopied;
    public string CopyLaunchText => launchCopied ? "已复制" : "复制";
    public async Task CopyLaunchLine()
    {
        if (launchLine is not { Refused: null } l) return;
        try
        {
            await Task.Run(() => Host.Core.LaunchLineOf(id, make: true));   // the folders it names, which opening the page doesn't make
            await Host.CopyText(l.Text);
            (launchCopied, Error) = (true, null);
        }
        catch (Exception e) { Error = "无法复制：" + e.Message; }
        Changed();
    }
    // read off the UI thread: it reads Steam's localconfig.vdf and the placement's manifest
    string? steamCacheNote;
    BackgroundRead<string?>? steamNoteRead;
    void ReadSteamCacheNote()
    {
        if (!Host.Platform.SteamLaunchLine) return;
        (steamNoteRead ??= new("Steam note", () =>
        {
            try { return Host.Core.SteamCacheNote(id); }
            catch (Exception) { return null; }   // e.g. the game is gone after a rescan
        }, n =>
        {
            if (n == steamCacheNote) return;
            steamCacheNote = n;
            Changed();
        })).Request();
    }
    public string SteamCacheNote => steamCacheNote ?? "";
    public bool HasSteamCacheNote => steamCacheNote != null;

    public bool HasCache => s.DiskBytes != null || otherBytes > 0 || s.Status is GameStatus.Warmed or GameStatus.Stale;
    // compiled before a verdict changed: the cache stays until the user clears it
    public string? KeptCacheNote => (s.Status == GameStatus.Unsupported || s.NoStutter != null) && s.WarmedAt != null && s.CacheOnDisk > 0
        ? $"SCSKiller 在该游戏被标记为{(s.NoStutter != null ? "无着色器卡顿" : "不受支持")}之前，已为其编译了 {DiskValue} 的缓存。"
        : null;
    public bool HasKeptCache => KeptCacheNote != null;

    // Maximum mode suggested for this game on this GPU vendor (Games.MaximumMode), or on for it alone
    public bool? MaximumPending { get; set; }
    public bool MaximumSuggested => s.MaximumSuggestion != null;
    // on for every game in Settings: the per-game switch changes nothing, so it isn't offered
    public bool HasMaximumLine => MaximumSuggested || s.MaximumForGame == true && !Host.Core.Settings.MaximumPlans;
    public string MaximumLine => MaximumSuggested
        ? "极致编译模式对此游戏有明显帮助：它会编译着色器的每一种组合，尽可能减少游玩时出现的卡顿。"
        : "已为此游戏开启极致编译模式。编译时间会更长，且占用更多磁盘空间。";
    public string? MaximumNote => string.Join(" ", new[] { s.MaximumSuggestion is { Length: > 0 } note ? note : null,
        MaximumSuggested && s.MaximumAddsBytes is { } adds ? $"在 AMD 显卡上，这将向驱动共享的 {Format.Bytes(AmdAppCache.DxcCacheCap)} 缓存中增加最多 {Format.Bytes(adds)}。" : null }.OfType<string>()) is { Length: > 0 } t ? t : null;
    public bool HasMaximumNote => MaximumNote != null;
    public string MaximumAction => MaximumSuggested ? "为此游戏开启" : "为此游戏关闭";
    public bool CanSetMaximum => MaximumPending == null && !Queued;
    public bool ShowClearCache => HasCache || CanCompile;
    // as Disk space shows it
    public string ClearCacheText => s.DiskBytes != null ? $"清除缓存 ({DiskValue})" : "清除缓存";
    public string ClearCacheTip => BaInProgressAny ? BaBusyTip : HasCache ? $"删除此游戏的着色器缓存（驱动缓存{(Host.Platform.SystemShaderCache ? "及 Windows 系统缓存" : "")}，以及由您选择的游戏自身缓存），以便下次游玩时进行冷启动测试"
        : "SCSKiller 会在首次编译或首次检测到游戏运行时识别其驱动缓存文件";

    // Rate this compile (CompatReport) and what players with a similar system reported (CompatFeed)
    string? ReportWhyNot => CompatReport.WhyNot(s, ScsKiller.PackGpu(Host.Core.Vendor.Vendor), Host.Core.LinuxReportsRefused);
    public bool CanReport => ReportWhyNot == null;
    public string ReportTip => ReportWhyNot ?? "告诉其他玩家在使用 SCSKiller 编译后该游戏的运行表现";
    public bool HasSimilar => s.Similar is SimilarSystems.Helped or SimilarSystems.NoHelp;
    public bool HasSimilarWarning => s.Similar == SimilarSystems.Warning;
    public bool HasRuntimeWarning => s.RuntimeWarning != null;
    public string RuntimeWarning => s.RuntimeWarning ?? "";
    public string SimilarText => s.Similar switch
    {
        SimilarSystems.Helped => "相似配置的用户反馈，编译后消除或改善了此游戏的卡顿现象。",
        SimilarSystems.NoHelp => "相似配置的用户反馈，编译未能改善此游戏的卡顿问题。",
        SimilarSystems.Warning => $"相似配置的用户反馈，{Name} 编译后运行表现变差。",
        _ => "",
    };

    public bool IsKnownStutter => s.KnownStutter != null;
    public string StutterReason => s.KnownStutter?.Reason ?? "";
    public bool IsNoStutter => s.NoStutter != null;
    public string NoStutterReason => s.NoStutter ?? "";
    /// <summary>The source as a link when it's a URL; otherwise <see cref="StutterCitation"/> shows it as text.</summary>
    public Uri? StutterUri => Uri.TryCreate(s.KnownStutter?.Source, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;
    public string StutterLinkText => StutterUri != null ? $"来源信息 ({s.KnownStutter!.Date})" : "";
    public string StutterCitation => s.KnownStutter is { } k && StutterUri == null ? $"来源：{k.Source} ({k.Date})" : "";
    public string EngineChip => s.Engine is { } e ? Format.Engine(e, e.Family == "Unreal" ? "Unreal Engine" : null) + Fmt.Guessed(e) + (e.Fork != null ? " · 自定义修改版" : "") : "未知引擎";
    public string ApiChip => s.Engine?.GraphicsApi.Replace("D3D", "DirectX ") ?? "";
    public bool HasApi => ApiChip.Length > 0 && !ShowApiPicker;

    // DirectX picker: every DirectX game (GameState.ApiDetected), or a choice is kept; the chip's place
    // or a carved game whose API nothing tells, packed shaders too: it may run Vulkan at run time (SDL)
    public bool ShowApiPicker => s is { ApiDetected: not null } or { ApiOverride: not null } && s.Engine is { Unsupported: null, Encrypted: false }
        || s.Engine is { Encrypted: false } e && Core.Carved.CarvedReader.Carved(e) && s.ApiDetected == Core.Unreal.UnrealRhi.Ambiguous;
    public bool? ApiPending { get; set; }
    public bool CanPickApi => ApiPending == null && !Host.Core.Queue.Any(q => q.GameId == id && Format.Running(q));   // its plan check too: the core refuses a change meanwhile
    IReadOnlyList<DxApi> ApiChoices => ScsKiller.ApiChoices(s, Host.Core.Vendor.Caps);
    public int ApiIndex => s.ApiOverride is { } a ? ApiChoices.ToList().IndexOf(a) + 1 : 0;
    /// <summary>The API of the picker's item <paramref name="i"/>; null = Auto.</summary>
    public DxApi? ApiAt(int i) => i <= 0 ? null : ApiChoices[i - 1];
    string[]? apiItems;
    public string[] ApiItems
    {
        get
        {
            var auto = s.Engine?.GraphicsApi.EndsWith(ScsKiller.RecordingSuffix, StringComparison.Ordinal) == true ? $"自动 (根据录制识别：{Dx(s.Engine.GraphicsApi)})"
                : s.ApiDetected == Core.Unreal.UnrealRhi.Ambiguous ? "自动 (DirectX 11 与 12)"
                : s.ApiDetected is { } d ? $"自动 ({Dx(d)})" : "自动";
            string[] items = [auto, .. ApiChoices.Select(ScsKiller.ApiName)];
            if (apiItems == null || !apiItems.SequenceEqual(items)) apiItems = items;   // the same array while the labels hold: a new one resets the selection
            return apiItems;
        }
    }
    static string Dx(string api) => ScsKiller.ApiName(api);
    public string? ApiOverruled => ScsKiller.ApiOverruled(s);
    public bool HasApiOverruled => ApiOverruled != null;
    public string? ApiKept => ScsKiller.KeptApisNote(s);
    public bool HasApiKept => ApiKept != null;
    public string AntiCheatChip => NoAntiCheat ? "无反作弊" : ScsKiller.AntiCheatName(s);
    public string AntiCheatTip => NoAntiCheat ? "未检测到反作弊：可正常进行录制"
        : s.RiskEligible ? $"该游戏包含 {ScsKiller.AntiCheatName(s)} 反作弊。录制器仅在您确认授权的测试会话中运行，风险由您自行承担。"
        : !Host.Platform.RecordAntiCheatGames ? NoAntiCheatSessions
        : ScsKiller.RiskNotHere(s) ?? $"{ScsKiller.AntiCheatName(s)} 会将额外的 {RecorderDll} 视为非法篡改：因此绝不会为此游戏进行录制";
    static string NoAntiCheatSessions => $"在 {Host.Platform.Name} 平台上暂不支持反作弊游戏录制会话。";
    public bool HasMiddleware => s.Middleware is { Count: > 0 };
    public bool HasModChip => GameRow.ModChip(s) != null;
    public string ModChip => GameRow.ModChip(s)?.Text ?? "";
    public string ModTip => GameRow.ModChip(s)?.Tip ?? "";
    public bool HasDeliveryChip => ScsKiller.DeliveryChip(s) != null;
    public string DeliveryChip => ScsKiller.DeliveryChip(s) ?? "";
    public string DeliveryTip => ScsKiller.DeliveryTip;
    IReadOnlyList<MiddlewareTag> Mw => s.Middleware ?? [];
    /// <summary>"Also compiles" only names tags with known pipelines (and DLSS on NVIDIA, by the driver) of a game that compiles; else what was detected.</summary>
    public string MiddlewareChip => Mw.Where(t => t.Pipelines > 0).Select(t => t.Label)
            .Concat(Mw.Any(t => t.Label == "DLSS") && GpuBackends.OwnDriver(Host.Core.Vendor) == GpuVendor.Nvidia ? ["DLSS（由显卡驱动编译）"] : []).ToList() is { Count: > 0 } also
            && s.Status != GameStatus.Unsupported
        ? "协同编译：" + string.Join(" · ", also) : "已检测到：" + string.Join(" · ", Mw.Select(t => t.Label));
    public string MiddlewareTip => "游戏目录旁的超分辨率/中间件 DLL。只要在任一游戏的录制中检测到该版本 DLL，其着色器管线就会随游戏一同编译"
        + "：可以在本机录制，FSR 和 XeSS 也能直接使用网络共享的管线数据"
        + (s.Plan is { MiddlewareItems: > 0 } p ? $" (本次编译计划包含：{Fmt.N(p.MiddlewareItems)} 个中间件管线)" : "") + ".\n\n"
        + string.Join("\n", Mw.Select(Fmt.TagTip));

    // The one tip left: a game that can't compile before a recording, or not its ray tracing (a plan's other gaps are the
    // coverage card's "what's left").
    public bool HasRecordTip => CanRecord && (s.Status == GameStatus.NeedsRecording && !ScsKiller.NeedsSteamStart(s) || RtAfter) && !RecordOn && s.NoStutter == null;
    bool Enough => ScsKiller.RecordedEnough(s);
    public string RecordTipTitle => Enough ? "录制更多游玩内容" : "录制 5 分钟游玩过程";
    public string RecordTip => RtPartial || RtAfter ? $"{TurnOnAndPlay}，并开启光线追踪游玩{(Enough ? "" : "约 5 分钟")}。 " + ScsKiller.RtWhy(Host.Core.Vendor.Caps, s.Engine)
            + (RtAfter ? " 其他所有着色器均已完成编译。" : " 其他所有着色器已可直接从游戏文件中编译。")
        : Enough ? Sentence(s.StatusReason) + "."
        : $"{TurnOnAndPlay}，照常游玩约 5 分钟。SCSKiller 将借此识别该游戏的着色器布局架构，随后即可开始编译。";
    // a collection's other game is recorded by its first game's recorder, switched on that game's page
    string TurnOnAndPlay => Collections.RecordedBy(s.Game.Id) is { } by ? $"请在《{Collections.Member(by)!.Value.Game.Name}》页面开启录制，然后游玩此游戏" : "开启录制并进入游戏";
    public bool ShowRecordAction => !RecordOn && CanToggleRecord && NoAntiCheat;   // an anti-cheat game records only through its session card

    // Shader coverage (PlanStats), in words: how much of what the planner found gets compiled, where it comes from, what's
    // left and what helps; every count in Details. No plan yet on a game that can compile: the card says when it's measured.
    PlanStats? P => s.Plan;
    // a compile that didn't help: the ring is greyed out, nothing of it counts for the game
    int? Pct => DriverChanged ? null : ScsKiller.ShownPercent(s);
    // the last compile filled the old driver's cache, which the new driver doesn't use
    bool DriverChanged => s.Status == GameStatus.Stale && s.StatusReason.StartsWith("driver changed:", StringComparison.Ordinal);
    public bool HasCoverageCard => ScsKiller.ShowsCoverage(s) || CanCompile;
    public bool HasPlan => P != null;
    public bool HasMeter => Pct != null;
    public bool LowCoverage => !s.CompileUnreached && (Pct is { } pct ? pct < 90 : Partial);   // more than 10% left out, like ScsKiller.IsPartial (the test for a plan without the count)
    public bool GoodCoverage => HasMeter && !LowCoverage;
    bool Dx11Only => P is { StageSets: 0, Generated: 0, D3D11Shaders: > 0 };
    public string CoverageHeadline => P == null ? "将在首次编译开始时确认"
        : s.CompileUnreached ? "此游戏未采用编译产物"
        : DriverChanged ? $"上次编译针对的是驱动版本 {s.WarmedDriverVersion}"
        : Dx11Only && Pct is null or 100 ? (Compiled ? "覆盖了" : "将覆盖") + "游戏文件中的所有 DirectX 11 着色器" + (Core.Planning.Planner.D3D11CoversVs(Host.Core.Vendor.Caps) || P.D3D11Draws > 0 ? "" : "（顶点着色器除外）")
        : Pct is not { } pct ? "将在下次编译时重新评估"
        : (Compiled || s.NewOnly ? "覆盖" : s.WarmedAt != null ? "上次编译覆盖了" : "将覆盖")
          + ScsKiller.CoverageWhat(s, pct);
    // the headline's own words under the ring while no recording has confirmed the engine rule
    public bool HasRuleNote => Pct == 100 && !Dx11Only && ScsKiller.RuleUnconfirmed(s);
    bool Rt => ScsKiller.NeedsRtRecording(s);
    bool RtUnseen => s.RtUnseen;
    bool RtInline => ScsKiller.RtInlineCovers(P);
    bool Compiled => s.WarmedAt != null && s.Status != GameStatus.Stale;
    VendorCaps Caps => Host.Core is ScsKiller k ? k.CapsOf(s.Game.Id) : Host.Core.Vendor.Caps;

    /// <summary>Where the plan's pipelines come from, in a player's words; the recording's row carries the community note,
    /// and without one the community database's status is its own row. Ray tracing: covered or what it needs.</summary>
    public IReadOnlyList<DetailRow> Sources => P is not { } p ? [] : new DetailRow?[]
    {
        p.Generated > 0 ? new(ScsKiller.BothApis(p) ? "DirectX 12（来自游戏文件）" : "来自游戏文件", Fmt.N(p.Generated),
            Pct == 100 && s.ShaderCount is { } n && p.Generated > n ? "每个着色器会被用于多种管线组合，因此管线数量会多于游戏原本的着色器数量。" : null) : null,
        !Vk && FromPlay > 0 ? new(RecordingSource, Fmt.N(FromPlay), CommunityLine) : null,
        p.FromDownload > 0 ? new("来自 Steam 着色器下载", Fmt.N(p.FromDownload)) : null,
        Vk && p.FromCommunity > 0 ? new("来自社区数据库", Fmt.N(p.FromCommunity)) : null,
        Vk && FromPlay > 0 ? new(FromPlayLabel, Fmt.N(FromPlay), CommunityLine) : null,
        p.CacheBudgetLeftOut is > 0 and var left ? new(ScsKiller.CacheBudgetTitle(Caps, s.Engine, s.PlanCacheCapGB), Fmt.N(left), ScsKiller.CacheBudgetNote(Caps, s.Engine, s.PlanCacheCapGB)) : null,
        s.Official is { } o ? new("官方录制数据", $"{o.Psos:N0} 个管线", $"由 SCSKiller 针对该版本发布，下载于 {o.DownloadedAt.LocalDateTime:d}") : null,
        s.Community == null && s.InCommunityDb is { } inDb && !HasDbTeaser ? new("社区数据库", inDb ? "已有该版本的录制数据" : "暂未收录") : null,
        p.MiddlewareItems > 0 ? new(p.MiddlewareSharedItems == 0 ? "超分辨率组件管线（从录制中习得）"
            : p.MiddlewareSharedItems == p.MiddlewareItems ? "超分辨率组件管线（来自共享包）" : "超分辨率组件管线（来自录制与共享包）", Fmt.N(p.MiddlewareItems)) : null,
        p.D3D11Shaders > 0 ? new(ScsKiller.BothApis(p) ? "DirectX 11（来自游戏文件）" : "DirectX 11 着色器", Fmt.N(p.D3D11Shaders)) : null,
        p.RtLibraries > 0 && s.RtSkipped ? new("光线追踪", "已跳过（根据您的设置）")
        : p.RtLibraries > 0 ? new("光线追踪特效", s.Engine?.NoRayTracing == true ? "此游戏已关闭" : s.Engine?.NoRtPipelines == true ? "内联光追，直接来自游戏文件" : p.RtUncovered == 0 ? "已完全覆盖" : s.RtToPlan ? "正在检查" : RtUnseen ? "录制期间未遇到" : RtInline ? "内联管线已覆盖" : !Rt ? "大部分已覆盖" : CanRecord ? "需要录制" : "未编译") : null,
    }.OfType<DetailRow>().ToList();
    public bool HasSources => Sources.Count > 0;

    // a Vulkan plan reads no game files: its pipelines are Steam's download and what play on this PC recorded, also a
    // DirectX game's under Proton made of Steam's alone. A DirectX plan with Steam's counts its download with its recorded
    bool Vk => s.Engine is { } e && Core.Vulkan.VulkanPlan.IsVulkan(e) || P is { FromDownload: not null, Generated: 0, SynthesizedTemplates: 0 };
    long FromPlay => P is { } p ? p.Recorded - (p.FromDownload ?? 0) - (p.FromCommunity ?? 0) : 0;
    string FromPlayLabel => P?.FromDownload == null ? "已录制管线" : "来自本机游玩录制";   // a plan from before the split was counted

    /// <summary>The recorded row's label: whose recordings the plan's recorded pipelines come from.</summary>
    string RecordingSource
    {
        get
        {
            if (UsedCommunity == null && s.Official == null) return "游玩录制管线";
            string[] from = [.. (UsedCommunity ?? s.Official)!.WithLocalRecording ? ["本机录制"] : Array.Empty<string>(),
                .. UsedCommunity != null ? ["社区数据库"] : Array.Empty<string>(), .. s.Official != null ? ["官方录制"] : Array.Empty<string>()];
            var text = from.Length == 1 ? from[0] : string.Join("与", from);
            return char.ToUpperInvariant(text[0]) + text[1..];
        }
    }
    /// <summary>The community recording the plan uses: not one that doesn't match the game's files.</summary>
    CommunityInfo? UsedCommunity => s.Community is { OtherVersion: false } c ? c : null;
    /// <summary>The community database's line under the recording row; null = nothing to say.</summary>
    string? CommunityLine => UsedCommunity is { } c
        ? $"已下载社区录制数据（下载时间：{c.DownloadedAt.LocalDateTime:d}）" + (s.RecordingSharedAt is { } at ? $" · 您的录制分享于 {at.LocalDateTime:d}" : "")
        : s.RecordingSharedAt is { } at2 ? $"已分享至社区数据库（时间：{at2.LocalDateTime:d}）" : null;

    // What's left and what helps. LeftOut is mostly Uncovered (root signature slots SCSKiller can't build) or, with a learned
    // lookup, stage sets no recording showed yet (no root signature / template): a longer recording is what helps there.
    // Ray tracing the plan can't compile comes first, in the status's words (ScsKiller.RtNote).
    enum Left { AntiCheat, EngineSlots, UnknownSlots, NotSeen, Recording, PlayedClean, PlayedCompiles, NothingKnown, PlayOnly }
    long Missing => P is { } m ? Math.Max(m.LeftOut, m.Uncovered) : 0;   // a plan without LeftOut: Uncovered only
    Left LeftCase => !CanRecord ? Left.AntiCheat
        : Missing > 0 ? P!.Uncovered * 2 >= Missing ? P.Recorded == 0 ? Left.EngineSlots : Left.UnknownSlots : Left.NotSeen
        : RecordOn ? Left.Recording
        : L is { } l ? PlayCompiles(l) == 0 ? Left.PlayedClean : Left.PlayedCompiles   // what was measured beats what's likely
        : P is { Recorded: > 0 } ? Left.NothingKnown
        : Left.PlayOnly;
    public string LeftText => LeftSentences(tipAsks: false);
    /// <summary>tipAsks: the record tip already gives the ray tracing note and the recording advice.</summary>
    string LeftSentences(bool tipAsks) => string.Join(" ", new string?[]
    {
        RtUnseen ? Sentence(ScsKiller.RtUnseenNote) + "。" : RtInline ? Sentence(ScsKiller.RtInlineNote) + "。" : !Rt || tipAsks ? null : CanRecord ? (HasDbTeaser ? ScsKiller.RtNeedsRecording : ScsKiller.RtNote(s.InCommunityDb, Host.Account?.HasDb == true)) + "。"
            : $"光线追踪特效尚未编译：需要录制数据，{(s.RiskEligible ? ScsKiller.RiskNoRecording : !NoAntiCheat ? $"但受 {ScsKiller.AntiCheatName(s)} 拦截" : s.RootUnconfirmed ? ScsKiller.ManualNoRecording : s.PreloadHeld is { } held ? ScsKiller.PreloadHeldNoRecording(held, s.PreloadHeldByOptions) : ScsKiller.StreamlineNoRecording)}。",
        LeftCase switch
        {
            Left.AntiCheat => s.RiskEligible ? "其他缺失内容会在游玩时实时编译：您可在本页面开启测试会话进行录制，风险自负。"
                : !NoAntiCheat ? $"其他缺失内容会在游玩时实时编译：{ScsKiller.AntiCheatName(s)} 拦截了用于捕获着色器的录制组件。"
                : s.RootUnconfirmed ? "其他缺失内容会在游玩时实时编译：请在上方确认游戏根目录以录制缺失的着色器。"
                : s.PreloadHeld != null ? "其他缺失内容会在游玩时实时编译。"
                : "其他缺失内容会在游玩时实时编译：NVIDIA DLSS 拦截了用于捕获着色器的录制组件。",
            Left.EngineSlots => ScsKiller.EngineSlotsNote(RecordOn),
            Left.UnknownSlots => "其余部分使用了 SCSKiller 尚无法重建的着色器槽位，因此仍会在游玩时实时编译。",
            Left.NotSeen => ScsKiller.NotSeenNote(RecordOn, P),
            Left.Recording => "录制已开启：您游玩中遇到的任何新内容都将在下次编译时添加。",
            Left.PlayedCompiles => $"上次游玩期间，仍有 {PlayCompiles(L!):N0} 个着色器需要在游戏运行时实时编译。" + (tipAsks ? "" : " 开启录制可捕获它们。"),
            _ when Rt || RtUnseen || RtInline => null,   // ray tracing is what's left
            Left.PlayedClean => "上次游玩时未缺失任何着色器。",
            Left.NothingKnown => "据 SCSKiller 所知，未缺失任何着色器。",
            _ => "少数特效仅在游玩时才会组装生成。" + (tipAsks ? "" : " 进行简短录制即可捕获它们。"),
        },
        !Guessed ? null : tipAsks ? "部分着色器状态为推测得出。" : "部分着色器状态为推测得出。更长时间的录制可使其更加精准。",
    }.OfType<string>());
    // per-stage plans (AMD): the planner warns that a wrong guess costs a compile in game
    bool Guessed => P is { } g && g.GuessedUnits > 0.1 * (g.ExactUnits + g.InferredUnits + g.GuessedUnits);
    // not twice: a game that needs a recording has the button in its tip
    public bool ShowLeftRecord => ShowRecordAction && !HasRecordTip && s.NoStutter == null && (LeftCase is Left.EngineSlots or Left.NotSeen or Left.PlayedCompiles or Left.PlayOnly || Guessed || Rt || RtUnseen || RtInline);
    // before the first compile there's nothing left yet. Recording on: what's missing comes from playing, without the button
    public bool ShowLeftCallout => HasPlan && (ShowLeftRecord || RecordOn && s.NoStutter == null && LeftCase is Left.NotSeen or Left.EngineSlots);
    public string LeftTitle => Rt || RtUnseen ? "光线追踪特效尚未编译" : "部分着色器仍会在游玩时实时编译";

    public string CoverageValue => Pct is { } pct ? $"{pct}%" : Dx11Only && !s.CompileUnreached ? "全部" : Format.Dash;
    public string CoverageLabel => s.CompileUnreached ? "未采用" : Pct != null || Dx11Only ? "已覆盖" : "尚未覆盖";
    /// <summary>The ring's arc as path data, in a 120 px box with a 12 px stroke; null when it isn't drawn in that colour.</summary>
    public string? GoodArc => GoodCoverage ? Arc() : null;
    public string? LowArc => LowCoverage ? Arc() : null;
    string? Arc()
    {
        const double c = 60, r = 54;
        double a = Math.Min(Pct ?? 0, 99.99) / 100 * 2 * Math.PI;
        if (a == 0) return null;   // a zero-length arc's round caps would draw a dot
        return string.Create(CultureInfo.InvariantCulture, $"M {c},{c - r} A {r},{r} 0 {(a > Math.PI ? 1 : 0)} 1 {c + r * Math.Sin(a):0.###},{c - r * Math.Cos(a):0.###}");
    }
    public bool HasTimeValue => Fmt.CompileTime(s) != null;
    public string TimeLabel => s.LastWarmTime != null ? "编译耗时" : "编译耗时（预估）";
    public string TimeValue => s.LastWarmTime is { TotalMinutes: >= 1, TotalHours: < 1 } t ? $"{t.Minutes} 分 {t.Seconds} 秒"   // measured: to the second
        : Format.Duration(Fmt.CompileTime(s));
    public bool HasDiskValue => s.DiskBytes != null || s.EstimatedCacheBytes != null;
    public string DiskLabel => s.DiskBytes != null ? "磁盘空间" : "磁盘空间（预估）";
    public string DiskValue => Format.Bytes(OnDisk ?? s.EstimatedCacheBytes);
    public bool HasDiskInUse => s.CacheOnDisk != null && Format.InUseShown(OnDisk, InUse);
    public string DiskInUse => $"已占用 {Format.Bytes(InUse)}";
    // Windows' dbs counted whole: what they hold isn't read
    long? InUse => s.CacheInUse + (s.WindowsCache ?? 0);
    // the kept-aside copy of what the driver removed takes disk space too
    long? OnDisk => s.DiskBytes + (s.Kept?.Frees ?? 0);
    public bool HasShaderCount => s.ShaderCount != null;
    public string ShaderCount => Fmt.N(ScsKiller.PageShaders(s));
    public string? ShaderCountTip => ScsKiller.AllShadersTip(s);
    // measured frames when the recorder timed them, else the compile count
    long? StutterCount => F is { } f ? Format.NoPlay(f.Low1PctFps) ? null : ShaderHitches.Count : L is { } l ? PlayCompiles(l) : null;
    public bool HasStutterTile => HasSession && StutterCount != null;
    public string StuttersValue => StutterCount is not { } n ? "" : n == 0 ? "无" : Fmt.N(n);
    public bool HasStutters => StutterCount > 0;
    public bool NoStutters => StutterCount == 0;
    // without frame times the count is of compiles, which may or may not have stuttered
    public string StuttersLabel => F != null ? "上次游玩时的卡顿次数" : "游玩期间实时编译次数";

    /// <summary>Every count, for power users.</summary>
    public IReadOnlyList<DetailRow> Details => new DetailRow?[]
    {
        P is { } p ? new("规划中的管线总数", Fmt.N(p.Recorded + p.Generated + p.MiddlewareItems + p.Variants)) : null,
        s.ShaderCount != null ? new("游戏文件中的着色器数", ShaderCount) : null,
        P is { StageSets: > 0 } ? new("发现的着色器组合数", Fmt.N(P.StageSets)) : null,
        P is { LeftOut: > 0 } ? new("未纳入的项", ScsKiller.LeftOutValue(P)) : null,
        P is { StageSets: 0, Uncovered: > 0 } ? new("因着色器槽位无法重建而未纳入", Fmt.N(P.Uncovered)) : null,
        P != null && !Vk ? new("已录制管线", Fmt.N(FromPlay)) : null,
        P is { FromDownload: { } dl } && (Vk || dl > 0) ? new("来自 Steam 着色器下载", Fmt.N(dl)) : null,
        Vk && P != null ? new(FromPlayLabel, Fmt.N(FromPlay)) : null,
        P != null && !Vk ? new("从游戏文件中构建", Fmt.N(P.Generated)) : null,
        P is { MiddlewareItems: > 0 } ? new("超分辨率组件管线", Fmt.N(P.MiddlewareItems)) : null,
        P is { Variants: > 0 } ? new("其他纹理过滤设置下的变体", Fmt.N(P.Variants)) : null,
        P is { D3D11Shaders: > 0 } ? new("DirectX 11 着色器", Fmt.N(P.D3D11Shaders)) : null,
        P is { RtLibraries: > 0 } ? new("光线追踪着色器库", $"{P.RtLibraries:N0}" + (s.Engine?.NoRtPipelines == true ? "（游戏未使用）" : s.RtSkipped ? "（已跳过，根据您的设置）" : P.RtUncovered > 0 ? $"（{P.RtUncovered:N0} 个未编译）" : "")) : null,
        P is { RtInline: > 0 } ? new("内联光线追踪着色器", Fmt.N(P.RtInline)) : null,
        s.Status != GameStatus.Unsupported && s.InCommunityDb is { } db && !HasDbTeaser ? new("适用于您版本的社区数据", db ? $"{s.CommunityDbPsos:N0} 个管线" : "暂无") : null,
        s.Community is { } c ? new("已下载的社区录制数据", c.OtherVersion ? "与您的游戏文件不匹配" : $"{c.Psos:N0} 个管线") : null,
        s.Official is { } o ? new("官方录制数据", $"{o.Psos:N0} 个管线") : null,
        P != null && !Vk ? new("合成管线模板", Fmt.N(P.SynthesizedTemplates)) : null,
        P != null && !Vk ? new("着色器布局", $"{P.RootSignatures:N0} · {RootSigSource(P)}") : null,
        P is { } u && u.ExactUnits + u.InferredUnits + u.GuessedUnits > 0 ? new("阶段单元 精确 / 推断 / 推测", $"{u.ExactUnits:N0} / {u.InferredUnits:N0} / {u.GuessedUnits:N0}") : null,
        P is { LayoutCoverage: > 0 } ? new("来自录制的顶点布局", $"{P.LayoutCoverage:P1}") : null,
        s.CacheOnDisk is { } disk ? new("显卡驱动缓存", Format.Bytes(disk) + "（实测）") : s.EstimatedCacheBytes is { } est ? new("显卡驱动缓存", "≈ " + Format.Bytes(est) + "（预估）") : null,
        s.WindowsCache is { } w ? new(ScsKiller.WindowsPart, Format.Bytes(w)) : null,
    }.OfType<DetailRow>().ToList();

    // "Shipped with the shaders" is the carver's case.
    string RootSigSource(PlanStats p) =>
        p.RootSigRuleVerified ? "已针对该引擎版本验证"
        : ScsKiller.ApiKey(s.Engine?.GraphicsApi) == "11" ? "无需提供（DirectX 11）"
        : p is { RootSignatures: 0, Recorded: > 0 } ? "包含于已录制管线中"
        : s.Engine?.Family == "Unreal" ? (p.Recorded > 0 ? "从录制中学习" : "尚未在此引擎版本上验证")
        : p.Recorded > 0 ? "从录制中学习" : "随游戏着色器附带";

    // Last compile: when, how long, what failed or was skipped and what to do; never compiled: what the first one costs.
    public bool HasCompileCard => CanCompile || s.LastWarmTime != null;
    public string CompileTitle => s.LastWarmTime == null ? "首次编译" : "上次编译";
    long Failed => s.LastWarmFailed ?? 0;
    long Skipped => s.LastWarmSkipped ?? 0;
    long Crashed => s.LastWarmCrashed ?? 0;
    long DriverSkipped => s.LastWarmDriverSkipped ?? 0;
    long Known => Math.Min(DriverSkipped, s.LastWarmKnown ?? 0);   // of DriverSkipped
    long CrashedBefore => Math.Min(Crashed, s.LastWarmCrashedBefore ?? 0);   // of Crashed
    long Unbuilt => s.LastWarmUnbuilt ?? 0;
    long Invalid => s.LastWarmInvalid ?? 0;
    long Unsupported => s.LastWarmUnsupported ?? 0;
    public string CompileText => s.WarmedAt is { } at
        ? $"编译于 {Fmt.When(at)}" + (s.LastWarmTime is { } t ? $"，耗时 {Format.Duration(t)}" : "") + $"，针对驱动版本 {s.WarmedDriverVersion}。"
          + (ScsKiller.NothingFailed(s) ? " 所有管线均编译成功。" : "")   // null = not known (a warm from before these were kept): no claim
          + (ScsKiller.RefusedLine(s) is { } refused ? " " + refused : "")
          + (OnDisk is { } c ? $" 占用磁盘空间：{Format.Bytes(c)}。" : "")
          + " " + OwnShaderStep
        : s.LastWarmTime is { } last ? $"着色器缓存已被清除。上次编译耗时 {Format.Duration(last)}。"
        : (s.EstimatedWarmTime is { } est ? $"预计耗时约 {Format.Duration(est)}，在后台运行，不影响您正常使用电脑。" : "在后台静默运行，不影响您正常使用电脑。")
          + (s.EstimatedCacheBytes is { } b ? $" 预计向显卡驱动着色器缓存添加约 {Format.Bytes(b)}。" : "");
    // no game file says whether a game runs its own precompile at startup: said on every compiled game's page, not its row
    public const string OwnShaderStep = "游戏启动时可能仍会运行其自身的着色器预处理步骤。";
    public string SkippedTitle => $"已跳过 {Skipped + Crashed + DriverSkipped + Unbuilt + Invalid + Unsupported:N0} 个";
    long NeedsRecording => Math.Min(Skipped, s.LastWarmNeedsRecording ?? 0);   // of Skipped: flagged by the community recording
    long NotInGame => Skipped - NeedsRecording;
    public string? SkippedText => s.WarmedAt == null || Skipped + Crashed + DriverSkipped + Unbuilt + Invalid + Unsupported == 0 ? null
        : string.Join(" ", new[]
        {
            NotInGame > 0 && P is { FromDownload: > 0, Generated: 0 } ? $"跳过了 {NotInGame:N0} 个：{ScsKiller.DownloadSkipped}。开启录制进入游戏可将它们补齐。"
            : NotInGame > 0 ? $"跳过了 {NotInGame:N0} 个：对应的着色器在当前游戏版本中不存在，无需处理。" : null,
            Crashed - CrashedBefore is > 0 and var crashedNow ? $"跳过了 {crashedNow:N0} 个：会导致显卡驱动崩溃。若游戏用到它们，游戏将自行尝试编译。" : null,
            DriverSkipped - Known is > 0 and var now ? $"跳过了 {now:N0} 个：显卡驱动在编译时发生崩溃或卡死，已自动跳过该失败点后续内容。" : null,
            Known + CrashedBefore > 0 ? ScsKiller.KnownNote(Known + CrashedBefore) + "。" : null,
            Unbuilt > 0 ? $"跳过了 {Unbuilt:N0} 个：SCSKiller 尚无法构建此部分管线。" : null,
            Invalid > 0 ? $"跳过了 {Invalid:N0} 个：未通过 Vulkan 校验（Steam 同样会跳过）。" : null,
            Unsupported > 0 ? $"跳过了 {Unsupported:N0} 个：当前 GPU 不支持其所需的功能特性。" : null,
            NeedsRecording > 0 ? $"{NeedsRecording:N0} 个管线需要本机录制：游戏或模组在运行时动态生成了这些着色器，未保存在游戏文件中。请开启“边玩边录”来捕获它们。" : null,
        }.OfType<string>());
    public bool HasSkipped => SkippedText != null;

    // Last play session (SessionStats, no timestamp): one bar, three segments, under the Record switch. Ready = from the
    // game's library or the driver cache; ray tracing state objects have their own line, so Requests isn't used.
    SessionStats? L => s.LastSession;
    public bool HasSession => L != null;
    public bool ShowSessionHint => L == null && CanRecord && SessionHint != null && !FramesOnly;
    public string? SessionHint => Format.SessionHint(s);
    public string SessionHeader => L is { } l ? $"上次游玩 · {Format.Duration(F?.Duration ?? l.Duration)}" : "";
    // ray tracing state objects included, so the headline agrees with the ray tracing line under it
    static long PlayCompiles(SessionStats l) => l.Compiles + l.StateObjectsCompiled;
    public string SessionVerdict
    {
        get
        {
            if (L is not { } l) return "";
            long play = PlayCompiles(l), started = l.StartupCompiles + l.StateObjectsStartupCompiled, ready = l.FromGameLibrary + l.CacheHits + l.StateObjectsReady;
            if (play + started == 0)
                return ready > 0 ? $"游玩期间无需编译任何着色器：游戏使用的全部 {ready:N0} 个着色器组合均已就绪。" : "游玩期间无需编译任何着色器。";
            // the longest compile call, of the PSOs only; whether a frame stuttered is the frame report's to say
            string worst = l.StateObjectsCompiled > 0 ? "" : play == 1 ? $" (耗时 {l.WorstCompileMs:0} 毫秒)" : $" (最长耗时 {l.WorstCompileMs:0} 毫秒)";
            return (play == 0 ? "游玩期间无需编译任何着色器。" : $"{play:N0} 需要在游玩时实时编译{worst}。")
                + (started > 0 ? $" {started:N0} 在游戏启动或加载时编译。" : "")
                + (ready > 0 ? $" 其余 {ready:N0} 个已就绪。" : "");
        }
    }
    public bool HasStateObjectLine => L is { } l && l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0;
    public string StateObjectLine => L is { } l ? $"光线追踪管线：{l.StateObjectsReady:N0} 个已就绪，{l.StateObjectsCompiled:N0} 个在游玩时实时编译"
        + (l.StateObjectsStartupCompiled > 0 ? $"，{l.StateObjectsStartupCompiled:N0} 个在启动或加载时编译。" : "。") : "";
    public bool HasRayQueryLine => L?.RayQueryRecompiles > 0;
    public string RayQueryLine => L is { RayQueryRecompiles: > 0 and var n }
        ? $"{n:N0} 个光追管线在每次游戏启动时仍会被驱动部分重新编译。" : "";

    // The last launch's frame times (FrameLog): its shader stutters, other hitches and loading, the 1% low of play.
    public FrameReport? F => s.LastFrames;
    List<Hitch> ShaderHitches => F?.Hitches.Where(h => h.Cause == HitchCause.Shader).ToList() ?? [];
    public bool HasFrames => F != null;
    public string FramesVerdict => F is not { } f ? "" : Format.Frames(f, ShaderHitches, Unsplit);
    // a DirectX 11 session (the frames-only recorder's too) has no creates: its hitches aren't split by cause
    bool Unsplit => FramesOnly || F is { D3D11: true };
    public bool SplitFrames => !Unsplit;
    public string OtherLegend => Unsplit ? "卡顿" : "其他异常帧";
    public string GraphEnd => Format.Duration(F?.Duration);
    public string FramesSummary => F is not { } f ? ""
        : $"启动阶段（游戏自身的着色器预处理及首次加载）：{Format.Duration(f.Startup)}。游玩阶段：持续 50 毫秒以上的严重卡顿有 {InPlay(f).Count(h => h.Ms >= 50):N0} 次，"
          + $"{InPlay(f).Count(h => h.Ms >= 100):N0} 持续 100 毫秒以上的严重卡顿有 {InPlay(f).Count(h => h.Ms >= 100):N0} 次。帧时间数据仅保留在本机。";
    // the same list while the report is the same: a new one makes the slow-frames list rebuild every row at each refresh
    public IReadOnlyList<DetailRow> HitchRows => hitchRows.Report == F ? hitchRows.Rows
        : (hitchRows = (F, F?.Hitches.Select(h => new DetailRow($"{Clock(h.At)}  {(Unsplit && h.Cause == HitchCause.Other ? "stutter" : Cause(h.Cause))}", Ms(h.Ms))).ToList() ?? [])).Rows;
    (FrameReport? Report, IReadOnlyList<DetailRow> Rows) hitchRows = (null, []);
    public static string HitchTip(Hitch h, bool neutral = false) => $"{Clock(h.At)} · {Ms(h.Ms)} · {(neutral ? "hitch" : Cause(h.Cause))}";
    static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    public bool HasHitchRows => F?.Hitches.Count > 0;

    // Earlier launches with frame times (FrameSessions), newest first; the one the section shows is left out. Read again
    // only when the game's state changes.
    public IReadOnlyList<DetailRow> EarlierSessions => earlier.For == s ? earlier.Rows
        : (earlier = (s, Host.Core.FrameHistory(id).Where(e => e.LaunchUnixMs != F?.LaunchUnixMs).Select(e => SessionRow(e, FramesOnly)).ToList())).Rows;   // each row's own D3D11 decides the rest
    (GameState? For, IReadOnlyList<DetailRow> Rows) earlier = (null, []);
    public bool HasEarlierSessions => EarlierSessions.Count > 0;
    // a cleared recording takes the last launch's frames, not the history
    public bool HasFramesCard => HasFrames || HasEarlierSessions;
    static DetailRow SessionRow(FrameSession e, bool unsplit) => new(
        $"{Fmt.Time(DateTimeOffset.FromUnixTimeMilliseconds(e.LaunchUnixMs).LocalDateTime, "g")} · {Format.Duration(e.Duration)}",
        Format.Session(e, unsplit));

    // the frames of play, between startup and quitting, whatever slowed them (a load in play too)
    static IEnumerable<Hitch> InPlay(FrameReport f) => f.Hitches.Where(h => FrameLog.InPlay(h.At.TotalMilliseconds, h.Ms, f.Startup.TotalMilliseconds) && h.Cause != HitchCause.Quitting);
    static string Ms(double ms) => Format.Ms(ms);
    static string Cause(HitchCause c) => c switch { HitchCause.Shader => "着色器编译卡顿", HitchCause.Other => "其他异常卡顿", HitchCause.Quitting => "正在退出",
        HitchCause.LoadingShaders => "场景加载并编译着色器", _ => "场景加载" };

    /// <summary>The session bar's three segments, as star widths.</summary>
    public double LibraryWidth => L?.FromGameLibrary ?? 0;
    public double HitsWidth => L?.CacheHits ?? 0;
    public double CompilesWidth => L?.Compiles ?? 0;
    public double CompilesMinWidth => L?.Compiles > 0 ? 4 : 0;
    public string FromLibrary => Fmt.N(L?.FromGameLibrary);
    public string CacheHits => Fmt.N(L?.CacheHits);
    public string Compiles => Fmt.N(L?.Compiles);
    public string SessionLabel => L is { } l
        ? $"{l.Requests:N0} 次管线请求：{l.FromGameLibrary:N0} 次来自游戏自自带库，{l.CacheHits:N0} 次直接命中显卡驱动缓存，{l.Compiles:N0} 次在游玩时实时编译"
          + (l.StartupCompiles > 0 ? $", {l.StartupCompiles:N0} 在游戏启动或加载时编译" : "")
          + (l.RayQueryRecompiles > 0 ? $", {l.RayQueryRecompiles:N0} 个光追管线每次启动需重新编译" : "")
          + (l.StateObjectsReady + l.StateObjectsCompiled + l.StateObjectsStartupCompiled > 0 ? $"。光线追踪管线：{l.StateObjectsReady:N0} 个已就绪，{l.StateObjectsCompiled:N0} 个在游玩时编译" : "")
          + (l.StateObjectsStartupCompiled > 0 ? $", {l.StateObjectsStartupCompiled:N0} 在游戏启动或加载时编译" : "")
        : "";

    public bool? RecordPending { get; set; }   // the state a recorder change that is running (off the UI thread) asked for
    // NVIDIA, DirectX 11: the recorder times frames and records nothing
    public bool FramesOnly => ScsKiller.Frames11Play(s.Engine, Host.Core.Vendor.Caps);
    // its switch waits for the pair to end
    bool PairFramesOnly => ScsKiller.Frames11(s.Engine, Host.Core.Vendor.Caps) && s.BeforeAfter is { Stage: not BeforeAfterStage.Done };
    public bool RecordOn => ShowOffline ? OfflineOn : !PairFramesOnly && (RecordPending ?? s.RecorderEffective);
    public bool CanToggleRecord => ShowOffline ? OfflinePending == null && !s.OfflineRunning
        : RecordPending == null && AlongsidePending == null && NoAntiCheat && s.RecorderSkip == null && !PairFramesOnly;
    public bool ShowUseDefault => RecordPending == null && s.RecorderOverride != RecorderOverride.Default && s.RecorderSkip == null && !PairFramesOnly && !s.RecorderForeign;
    public string UseDefaultText => $"使用默认设置 ({(ScsKiller.RecordsByDefault(Host.Core.Settings, s) ? "开启" : "关闭")})";
    string RecorderDll => Fmt.RecorderDll(s);
    string Kept => ScsKiller.SessionKeptNote(s) is { } kept ? " " + kept : "";
    // another copy's recorder in the folder is said on every line the switch shows: a skip's line has no room for the reconcile's note
    public string RecordNote => RecordNoteText is var t && s.RecorderForeign && !t.Contains(Sentence(ScsKiller.ForeignNote)) ? $"{t} {ScsKiller.ForeignHereNote}" : t;
    string RecordNoteText => ScsKiller.CrashNote(s.Game) is { } crash ? crash
        : Risk
        ? (s.AlwaysRecord ? "每次启动均进行录制。" : "每次录制会话在启动前均需手动确认。") + Kept
        : ShowOffline
        ? $"{ScsKiller.AntiCheatName(s)} 拦截了录制器，但您可以通过离线会话自行承担风险启用录制。" + Kept
        : s.RiskNoLaunch ? ScsKiller.NoLaunchNote(Fmt.StoreName(s.Game))
        : !NoAntiCheat
        ? Host.Platform.RecordAntiCheatGames ? ScsKiller.RiskNotHere(s) ?? $"不可用：{ScsKiller.AntiCheatName(s)} 会将额外的 {RecorderDll} 视为非法篡改，因此该游戏仅直接从其游戏文件进行编译。" : NoAntiCheatSessions
        : PairFramesOnly ? "仅统计帧时间，用于编译前后对比测试。"
        : s.RecorderSkip == ScsKiller.SkipManual ? "在您确认游戏目录前不可用（请点击上方的“游戏根目录…”）：SCSKiller 在录制前需要扫描整个目录以排查反作弊。"
        : s.RecorderSkip == ScsKiller.SkipModNotChainable ? $"Not available: {ScsKiller.NotChainableReason(s.RecorderMod, RecorderDll)}."
        : s.RecorderSkip == ScsKiller.SkipAgsMod && s.AgsMod is { } agsMod && !(agsMod == "ReShade" && s.ShaderModLayer && s.ShaderModAsD3D12) ? $"Not available: {ScsKiller.SkipAgsMod}. {ScsKiller.AgsModHint(agsMod)}"   // else ShaderModNote says it
        : s.RecorderSkip is { } skip ? $"Not available: {skip}."
        : (s.Engine is { } vk && Core.Vulkan.VulkanPlan.IsVulkan(vk) ? ScsKiller.VulkanRecorderNote
            : FramesOnly ? $"在游戏目录旁添加小巧的 {Fmt.RecorderFiles(s)}，用于统计帧时间以生成上方的图表，不录制其他内容。"
            : $"在游戏目录旁添加小巧的 {Fmt.RecorderFiles(s)}，用于捕获编译计划遗漏的内容并统计帧时间，以便在页面上直观展示导致卡顿的原因。")
          + " 可随时移除。如果游戏包含 SCSKiller 未能检测到的反作弊，请勿开启此项。"
          + (s.RecorderNote is { } note && note != s.RecorderFailure ? $" ({Sentence(note)})" : "")
          + (s.RecorderRefused is { } why && !(s.Status == GameStatus.NeedsRecording && ScsKiller.NeverRecorded(s)) ? " " + why : "");   // else the status says it

    // A mod's d3d12.dll where the recorder goes (ReShade, a wrapper): off = the game isn't recorded; on = the recorder chains to it
    public bool HasMod => s.RecorderMod != null && s.RecorderSkip is not (ScsKiller.SkipAntiCheat or ScsKiller.SkipShaderMod or ScsKiller.SkipManual or ScsKiller.SkipUnsupported or ScsKiller.SkipNotDx12 or ScsKiller.SkipStreamline or ScsKiller.SkipCrashes);
    public string AlongsideTitle => $"与 {s.RecorderMod} 协同录制";
    public string AlongsideNote => $"{s.RecorderMod} 的 d3d12.dll 会在录制器注入期间临时重命名，并在录制器移除时原样恢复。";
    public bool? AlongsidePending { get; set; }
    public bool AlongsideOn => AlongsidePending ?? s.RecordAlongsideMod;
    public bool CanToggleAlongside => AlongsidePending == null && RecordPending == null;

    // An eligible EasyAntiCheat game (Games.OfflineEac) or an anti-cheat game allowed in Settings (RiskEligible): the card's
    // switch allows sessions, each confirmed before it starts
    public bool ShowOffline => s.OfflineEligible || s.RiskEligible;
    public bool Risk => s.RiskEligible;
    public bool? OfflinePending { get; set; }
    public bool OfflineOn => OfflinePending ?? s.OfflineRecord;
    public bool ShowOfflineOn => ShowOffline && OfflineOn;
    public bool OfflineStarting { get; set; }
    public bool CanStartOffline => s.OfflineRecord && OfflinePending == null && !s.OfflineRunning && !s.Playing && !OfflineStarting;
    // a game a session is offered for, with its switch on: every launch records without the per-launch confirmation
    public bool ShowAlways => Risk && OfflineOn;
    public bool? AlwaysPending { get; set; }
    public bool AlwaysOn => AlwaysPending ?? s.AlwaysRecord;
    public bool CanToggleAlways => AlwaysPending == null && OfflinePending == null && !OfflineStarting;
    public bool ShowSessionButton => !(Risk && AlwaysOn) || s.OfflineRunning;
    public string OfflineButtonText => Risk
        ? s.OfflineRunning ? "录制会话运行中…" : "录制测试会话（自行承担风险）"
        : s.OfflineRunning ? "离线录制会话运行中…" : "脱机运行并录制（无需 EasyAntiCheat，自行承担风险）";
    public string OfflineRiskText => Risk ? RiskText(s) : OfflineRisk;
    public const string OfflineRisk = "将在不加载 EasyAntiCheat 的情况下离线启动游戏。SCSKiller 会在游戏退出时清除相关文件（若游戏崩溃，则在下次登录时清除）。若在线游玩时残留这些文件，可能导致封号风险。必须保持 Steam 运行。切勿在录制会话期间卸载 SCSKiller。";
    public static string RiskText(GameState g) => $"该游戏包含 {ScsKiller.AntiCheatName(g)} 反作弊。开启录制器需由您自行承担风险。SCSKiller 对账号被封禁概不负责。";
    public string? SessionNote => s.SessionNote;
    public bool HasSessionNote => s.SessionNote != null;
    public string? RecorderFailure => s.RecorderFailure is { } f ? $"录制器{f}。" : null;
    public bool HasRecorderFailure => s.RecorderFailure != null;

    public bool HasRecording => s.RecordingBytes > 0;
    public string RecordingSize => $"录制数据占用 {Format.Bytes(s.RecordingBytes)}（包含游戏文件夹及 SCSKiller 的本地副本）";
    public bool RecordingPaused => s.RecordingPaused;
    public string RecordingPausedText => ScsKiller.PausedNote(Host.Core.Settings) + "。可在“设置”中调高上限，或清空当前录制。";
    public bool ClearingRecording { get; set; }
    public bool CanClearRecording => !ClearingRecording && !s.Playing;
    public string ClearRecordingTip => s.Playing ? "请先关闭游戏" : "删除已录制的管线数据。录制器文件将保留在游戏目录中";

    public bool Playing => s.Playing;
    public bool Refreshing { get; private set; }
    public bool CanRefresh => !Refreshing;
    public async void RefreshGame()
    {
        if (Refreshing) return;
        Refreshing = true;
        Changed();
        try { await Task.Run(() => Host.Core.RefreshGame(id)); Error = null; }
        catch (Exception e) { Error = e.Message; }
        Refreshing = false;
        ReadCaches();
        Refresh();
    }
}

/// <summary>A label and its value (the game page's sources and Details); Note: a line under the label.</summary>
public sealed record DetailRow(string Label, string Value, string? Note = null)
{
    public bool HasNote => Note != null;
}
