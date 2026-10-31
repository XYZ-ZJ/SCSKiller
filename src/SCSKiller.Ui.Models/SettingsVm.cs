using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Ui.Models;

public sealed class SettingsVm : Bindable
{
    public static readonly (string Label, CacheLimit Limit)[] Sizes =
    [
        ("512 MB", new(512L << 20, false)), ("1 GB", new(1L << 30, false)), ("4 GB", new(4L << 30, false)), ("5 GB", new(5L << 30, false)),
        ("10 GB", new(10L << 30, false)), ("12 GB", new(12L << 30, false)), ("16 GB (驱动默认值)", new(16L << 30, true)), ("24 GB", new(24L << 30, false)),
        ("32 GB", new(32L << 30, false)), ("48 GB", new(48L << 30, false)), ("64 GB", new(64L << 30, false)), ("100 GB", new(100L << 30, false)),
        ("无限制", new(null, false)),
    ];
    static readonly int DefaultSize = Array.FindIndex(Sizes, s => s.Limit.Bytes == 100L << 30);
    public static readonly string[] SizeLabels = Sizes.Select(s => s.Label).ToArray();

    static Settings S { get => Host.Core.Settings; set => Host.Core.Settings = value; }
    void Save(Settings s) { S = s; Refresh(); }
    static IGpuVendorBackend V => Host.Core.Vendor;
    // The driver cache's limit and usage, the drive's free space and NVIDIA's Auto Shader Compilation, read off the UI thread; null until the first read.
    (CacheLimit? Limit, CacheUsage Usage, string Free, AutoShaderState? Auto)? read;
    readonly BackgroundRead<(CacheLimit?, CacheUsage, string, AutoShaderState?)> cache;
    long? amdUsed;   // AMD: the DirectX 12 cache, which its fixed limit counts

    public SettingsVm()
    {
        cache = new("Settings cache", () =>
        {
            var usage = V.GetCacheUsage();
            string free;
            drive = DriveOf(usage);
            amdUsed = (V as AmdBackend)?.AppCache.DxcBytes();
            try { free = Format.Bytes(new DriveInfo(drive).AvailableFreeSpace); } catch (Exception) { free = Format.Dash; }
            return (V.GetCacheLimit(), usage, free, V.GetAutoShaderCompilation());
        }, r =>
        {
            // Preselect the current custom limit, else 100 GB (the mockup's suggestion over the 16 GB default).
            if (read == null) SizeIndex = Array.FindIndex(Sizes, s => !s.Limit.IsDriverDefault && s.Limit == r.Item1) is >= 0 and var i ? i : DefaultSize;
            // The checkbox and level show the driver's state after every read (an apply's re-read included); Medium when off.
            AutoOn = r.Item4 is { Level: not AutoShaderCompilation.Off };
            AutoLevelIndex = r.Item4 is { Level: not AutoShaderCompilation.Off and var l } ? (int)l - 1 : 1;
            read = r;
            Changed();
        });
        cache.Request();
    }

    /// <summary>A read of the driver cache has applied (the limit, usage and Auto Shader Compilation show).</summary>
    public bool Read => read != null;

    public string CacheTitle => $"{(Mesa ? "Mesa" : Fmt.Vendor(V.Vendor))} shader cache size";
    public bool IsNvidia => V.Vendor == GpuVendor.Nvidia && !Mesa;
    public bool ShowsAutoShader => IsNvidia && Host.Platform.AutoShaderCompilation;
    // NVIDIA's Linux driver: a cache folder per game, its size SCSKiller's (NvidiaLinux.PlacedCacheSize)
    bool PerGameNvidia => IsNvidia && V.Caps.PerGameCacheCap != null;
    // Mesa's RADV, ANV and NVK: each Steam game's folder, one file with no size limit
    bool Mesa => MesaLinux.Is(V.Caps);

    public static readonly string[] AutoLevels = ["低", "中", "高"];   // AutoShaderCompilation.Low..High, as the NVIDIA App names them
    public bool? AutoOn { get; set; }
    public int AutoLevelIndex { get; set; } = 1;
    public AutoShaderCompilation AutoChoice => AutoOn == true ? (AutoShaderCompilation)(Math.Clamp(AutoLevelIndex, 0, 2) + 1) : AutoShaderCompilation.Off;
    public string AutoShaderText => read == null ? Format.Dash : read.Value.Auto switch
    {
        null => "无法读取该设置",
        { Level: AutoShaderCompilation.Off } => "当前: 关闭",
        { TaskReady: true, Level: var l } => $"Now: On, {l}",
        _ => "设置已开启但 NVIDIA 任务未就绪：点击“应用”进行修复",
    };
    public string? AutoMessage { get; set; }
    public InfoBarSeverity AutoSeverity { get; set; }
    public bool HasAutoMessage => AutoMessage != null;
    public string CacheIntro => Mesa ? "每个游戏拥有独立的缓存，存放于 Steam 的着色器缓存目录中。"
        : V.Caps.PerGameCacheCap != null ? "每个游戏拥有独立的缓存。当缓存满时，驱动会自动删除该游戏较旧的缓存文件，进而再次引发卡顿。"
        : (IsNvidia ? "全局显卡驱动设置，与 NVIDIA App › 图形 › 着色器缓存大小 相同。" : "全局显卡驱动设置。 ")
        + (KeptOn ? "当缓存满时，驱动会自动删除较旧的条目。被保留备份的游戏会自动放回。" : "当缓存满时，驱动会自动删除较旧的条目，这些游戏将再次出现卡顿。");
    public bool Configurable => V.Caps.CacheSizeConfigurable;
    public bool NotConfigurable => !Configurable;
    public string? AmdAlwaysOn => ScsKiller.AmdAlwaysOnLine(V);
    public bool HasAmdAlwaysOn => AmdAlwaysOn != null;
    public string NotConfigurableText => Mesa ? "Mesa 将每个游戏的着色器保存在单个无大小限制的文件中，因此无需设置缓存大小。"
        : V.Vendor == GpuVendor.Amd
        ? $"AMD 显卡驱动的 DirectX 12 着色器缓存固定为 {Format.Bytes(AmdAppCache.DxcCacheCap)}，无法调整大小。"
          + "超出此限制后，驱动会在游戏启动时自动移除最久未玩的缓存" + (KeptOn ? "。已开启单独保留的游戏会自动放回。" : "，SCSKiller 会提示这些游戏需要重新编译。")
        : V.Vendor == GpuVendor.Intel
        ? $"Intel 显卡驱动为每个游戏保留最多 {Format.Bytes(IntelBackend.PerGameCap)} 的着色器，无法调整大小。"
          + "超出此限制后，驱动会丢弃该游戏较早的着色器，因此 SCSKiller 会优先编译可能性最高的着色器。"
        : PerGameNvidia ? $"SCSKiller 为每个游戏分配 {Format.Bytes(NvidiaLinux.PlacedCacheSize)} 的 NVIDIA 着色器缓存，无法调整大小。"
        : $"SCSKiller 尚无法修改 {Fmt.Vendor(V.Vendor)} 显卡驱动的缓存大小，仅能显示当前已使用量。"
          + "如果游戏缓存被挤占丢弃，请在显卡控制面板中调高缓存上限。";

    public string CurrentLimit => read == null ? Format.Dash : read.Value.Limit switch
    {
        null => "未知",
        { Bytes: null } => "无限制",
        { Bytes: 0 } => "已禁用",
        { Bytes: { } b, IsDriverDefault: var d } => Format.Bytes(b) + (V.Caps.PerGameCacheCap != null ? " / 游戏" : "") + (d ? " (驱动默认)" : ""),
    };
    public string Used => read is { Usage: var u } ? (u.UpperBound ? "≤ " : "") + Format.Bytes(u.BytesOnDisk) : Format.Dash;
    public bool HasAmdUse => amdUsed != null;
    public double AmdPercent => amdUsed is { } b ? Math.Min(100, 100.0 * b / AmdAppCache.DxcCacheCap) : 0;
    public string AmdUse => amdUsed is { } b ? $"DirectX 12 cache: {Format.Bytes(b)} of {Format.Bytes(AmdAppCache.DxcCacheCap)}"
        + (b >= AmdAppCache.DxcCacheCap * 9 / 10 ? ". Nearly full: the driver removes the least recently used games' caches when a game starts." : "") : "";
    public bool IsAmd => V.Vendor == GpuVendor.Amd && !Mesa;
    bool KeptOn => IsAmd && S.KeepCachesAside;
    public bool KeepCachesAside { get => S.KeepCachesAside; set { if (value != S.KeepCachesAside) Save(S with { KeepCachesAside = value }); } }
    public static IReadOnlyList<GameState> KeptGames => Host.Core.Games.Where(g => g.Kept != null).OrderByDescending(g => g.Kept!.Frees).ThenBy(g => g.Game.Name).ToList();
    public bool HasKept => S.KeepCachesAside && KeptGames.Count > 0;
    public string KeptSummary => KeptGames is var k && k.Count > 0
        ? $"{k.Count} game{(k.Count == 1 ? "" : "s")}, {Format.Bytes(k.Sum(g => g.Kept!.Bytes))} kept aside · frees {Format.Bytes(k.Sum(g => g.Kept!.Frees))}" : "";
    // through a link (the cache moved to another drive with a junction): the drive its files are on
    static string DriveOf(CacheUsage? u) => (u?.Path is { Length: > 0 } p ? GameFiles.VolumeOf(GameFiles.FinalPath(p) ?? p) : null)
        ?? GameFiles.VolumeOf(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? Path.GetPathRoot(Environment.CurrentDirectory)!;
    string? drive;   // read with the usage: resolving a link opens the folder
    public string FreeLabel => $"Free on {(drive ?? DriveOf(null)).TrimEnd('\\')}";
    public string Free => read?.Free ?? Format.Dash;

    int sizeIndex = DefaultSize;
    public int SizeIndex { get => sizeIndex; set { if (value >= 0) sizeIndex = value; } }   // -1: the list was replaced
    public string? ApplyMessage { get; set; }
    public InfoBarSeverity ApplySeverity { get; set; }
    public bool HasApplyMessage => ApplyMessage != null;
    public void Refresh()
    {
        Changed();
        cache.Request();
    }

    public bool ShowsDriverModes => Host.Platform.DriverUpdateModes;
    public string? DriverModesNote => ShowsDriverModes ? null : $"On {Host.Platform.Name}, rebuild them from the Library, where they show as Needs rebuilding.";
    public bool HasDriverModesNote => DriverModesNote != null;
    public int DriverMode
    {
        get => DriverUpdateChoice.Of(S);
        set
        {
            if (value < 0 || value == DriverMode) return;
            Save(DriverUpdateChoice.With(S, value));
            try { Host.Core.ApplyDriverUpdateMode(); }  // user action: registers or removes the sign-in check
            catch (Exception e) { ApplyMessage = "无法更新登录检查状态: " + e.Message; ApplySeverity = InfoBarSeverity.Error; Changed(); }
        }
    }
    public bool? PauseWhileGaming { get => S.PauseWhileGaming; set { if (value is { } v && v != S.PauseWhileGaming) Save(S with { PauseWhileGaming = v }); } }
    public double BackgroundThreads { get => UsedBackground; set { if ((int)value != UsedBackground) Save(S with { BackgroundThreads = (int)value }); } }
    static int UsedBackground => ScsKiller.UsedThreads(S.BackgroundThreads);
    public string BackgroundThreadsText => $"{UsedBackground} of {Environment.ProcessorCount}";
    public double MaxThreads => Environment.ProcessorCount;
    public bool HasLastRebuilt => LastRebuilt != "";
    public string LastRebuilt => Host.Core.Games.Where(g => g.WarmedAt != null && g.WarmedDriverVersion != null).MaxBy(g => g.WarmedAt)?.WarmedDriverVersion is { } v ? $"Last rebuilt for driver {v}" : "";

    public bool? MaximumPlans { get => S.MaximumPlans; set { if (value is { } v && v != S.MaximumPlans) Save(S with { MaximumPlans = v }); } }
    public bool? CompileRayTracing { get => S.CompileRayTracing; set { if (value is { } v && v != S.CompileRayTracing) Save(S with { CompileRayTracing = v }); } }
    public bool? ShareRecordings { get => S.ShareRecordings; set { if (value is { } v && v != S.ShareRecordings) Save(S with { ShareRecordings = v }); } }
    public bool? ShareDiagnostics { get => S.ShareDiagnostics; set { if (value is { } v && v != S.ShareDiagnostics) Save(S with { ShareDiagnostics = v }); } }
    public bool? ActiveCheck
    {
        get => SCSKiller.Core.App.ActiveCheck.Allowed(S);
        set { if (value is { } v && v != SCSKiller.Core.App.ActiveCheck.Allowed(S)) Save(SCSKiller.Core.App.ActiveCheck.Answered(S, v)); }
    }
    public void GamesChanged() => Changed();   // no driver-cache re-read (Refresh)
    public bool RecordAllGames { get => S.RecordAllGames; set { if (value != S.RecordAllGames) Save(S with { RecordAllGames = value }); } }   // the core reconciles off this thread
    public string RecordAllGamesNote => "在无反作弊的游戏中添加小巧的 d3d12.dll 或 d3d11.dll，以捕获编译计划遗漏的着色器并统计每帧耗时，以便在游戏页面直观展示卡顿原因。"
        + $"帧时间数据仅保留在本机。绝不会用于包含反作弊的游戏{(ShowsRecordAntiCheatGames ? "（除非您在下方明确允许）" : "")}。"
        + "关闭此项将从所有未主动开启录制的游戏中移除录制器。";
    public bool ShowsRecordAntiCheatGames => Host.Platform.RecordAntiCheatGames;
    // where the OS has no risk sessions it reads as off and is never saved: settings.json keeps the user's value
    public bool? RecordAntiCheatGames
    {
        get => ShowsRecordAntiCheatGames && S.RecordAntiCheatGames;
        set { if (ShowsRecordAntiCheatGames && value is { } v && v != S.RecordAntiCheatGames) Save(S with { RecordAntiCheatGames = v }); }   // off: the core clears each game's switch off this thread
    }
    /// <summary>"Recording in 23 games · 5 skipped: anti-cheat"; not-DX12 and unsupported games aren't counted.</summary>
    public string RecordingCount
    {
        get
        {
            var games = Host.Core.Games;
            var skipped = games.Where(g => g.RecorderSkip is ScsKiller.SkipAntiCheat or ScsKiller.SkipShaderMod or ScsKiller.SkipForeignDll or ScsKiller.SkipModNotChainable
                    or ScsKiller.SkipVulkanMod or ScsKiller.SkipNeedsAdmin or ScsKiller.SkipPackageD3D12 or ScsKiller.SkipStreamline or ScsKiller.SkipShaderInjector or ScsKiller.SkipAgsMod)
                .GroupBy(g => g.RecorderSkip).OrderByDescending(x => x.Count()).Select(x => $" · {x.Count()} skipped: {x.Key}");
            int n = games.Count(g => g.RecorderInstalled);
            return $"Recording in {n} game{(n == 1 ? "" : "s")}" + string.Concat(skipped);
        }
    }

    public static readonly string[] TraversalChoices = TraversalFix.SettingsChoices;
    public bool ShowsTraversal => TraversalFix.Available;
    public int TraversalIndex
    {
        get => TraversalFix.SettingsIndex(S);
        set { if (value >= 0 && value != TraversalFix.SettingsIndex(S)) Save(TraversalFix.Answered(S, TraversalFix.SettingsAt(value))); }   // the core rewrites the recorders' inis
    }
    public bool TraversalLowVram => TraversalFix.LowVram(Host.Core.Vendor.Gpu) == true;
    public string TraversalNote => TraversalFix.SettingsLine + (TraversalLowVram ? "" : " " + TraversalFix.VramLine) + " " + TraversalFix.PerGameLine;
    public string TraversalWarning => TraversalFix.LowVramWarning;

    public static readonly int[] RecordingLimits = ScsKiller.RecordingLimits;
    public static readonly string[] RecordingLimitLabels = RecordingLimits.Select(ScsKiller.LimitText).ToArray();
    public int RecordingLimitIndex
    {
        get => Array.IndexOf(RecordingLimits, S.RecordingLimitMB);   // -1: a value set elsewhere, shown blank
        set { if (value >= 0 && RecordingLimits[value] != S.RecordingLimitMB) Save(S with { RecordingLimitMB = RecordingLimits[value] }); }   // the core reconciles off this thread
    }
    /// <summary>Each game with a recording on disk, largest first: its size, and whether it reached the limit.</summary>
    public IReadOnlyList<DetailRow> Recordings => Host.Core.Games.Where(g => g.RecordingBytes > 0).OrderByDescending(g => g.RecordingBytes)
        .Select(g => new DetailRow(g.Game.Name, Format.Bytes(g.RecordingBytes), g.RecordingPaused ? ScsKiller.PausedNote(S) : null)).ToList();
    public bool HasRecordings => Host.Core.Games.Any(g => g.RecordingBytes > 0);
    public string RecordingSpace => Host.Core.Games.Where(g => g.RecordingBytes > 0).ToList() is { Count: > 0 } r
        ? $"Recordings use {Format.Bytes(r.Sum(g => g.RecordingBytes))} in {r.Count} game{(r.Count == 1 ? "" : "s")}" : "磁盘上暂无录制数据";
    public bool ShowsStartAtSignIn => Host.Platform.StartAtSignIn;
    /// <summary>The entry as read back where it can be (<see cref="Host.StartsAtSignIn"/>), else the setting.</summary>
    public bool? StartWithWindows
    {
        get => Host.StartsAtSignIn() ?? S.StartWithWindows;
        set
        {
            if (value is not { } v || v == StartWithWindows) return;
            S = S with { StartWithWindows = v };   // no Refresh before it applies: the read-back would undo the click
            _ = ApplyStartAsync();
        }
    }
    async Task ApplyStartAsync()
    {
        try { await Host.StartWithWindowsChanged(); }
        finally { Changed(nameof(StartWithWindows)); }
    }
    public static string StartText { get; } = OperatingSystem.IsWindows() ? "开机时自动启动 SCSKiller" : "登录系统时自动启动";
    public static string StartNote { get; } = OperatingSystem.IsWindows() ? "软件常驻于任务栏托盘区域，以便在驱动更新后及时提醒您重新构建缓存。"
        : "It waits in the background to offer a rebuild after a driver update.";
    // no sign-in task on Linux: the running app notifies
    public static string AskNote { get; } = OperatingSystem.IsWindows() ? "通过系统通知提供“立即编译”、“空闲时编译”或“跳过”选项，不会擅自开始编译。"
        : "运行期间将通过通知提供“立即编译”、“空闲时编译”或“跳过”选项。";
    // Linux has no input idle time: the background compile starts at once and pauses only for a game
    public static string WhenIdleNote { get; } = OperatingSystem.IsWindows() ? "一旦检测到鼠标或键盘操作立即暂停，并在再次空闲时自动继续。"
        : "Rebuilds in the background and pauses while a game runs.";
    public bool? BeforeAfter { get => S.BeforeAfter; set { if (value is { } v && v != S.BeforeAfter) Save(S with { BeforeAfter = v }); } }
    public int NewShadersChoice
    {
        get => (int)NewShaders.Global(S);
        set
        {
            if (value < 0 || value == NewShadersChoice) return;
            Save(S with { OnNewShaders = (NewShadersMode)value });
            Host.NewShadersChanged();
        }
    }
    public bool? NewShadersRightAway { get => S.NewShadersRightAway; set { if (value is { } v && v != S.NewShadersRightAway) Save(S with { NewShadersRightAway = v }); } }
    public string ScanAtStartNote => "重新扫描各游戏目录的变动。关闭后：显示上次列表，仅在手动刷新时重新读取。 "
        + $"when it exits, or when {Host.Platform.UpdatingStores} updates it.";
    public bool? ScanAtStart { get => S.ScanAtStart; set { if (value is { } v && v != S.ScanAtStart) Save(S with { ScanAtStart = v }); } }
    public bool? HddMode { get => S.HddMode; set { if (value is { } v && v != S.HddMode) Save(S with { HddMode = v }); } }
    public IReadOnlyList<HddDriveRow> HddDrives => [.. ScsKiller.HddDriveChoices(Host.Core.Games, S)
        .Select(d => new HddDriveRow(d, S.HddDrives?.Contains(d, StringComparer.OrdinalIgnoreCase) == true, (d, on) => Save(ScsKiller.WithHddDrive(S, d, on))))];
    public bool ShowsHddDrives => !S.HddMode && (HddDrives.Count > 1 || S.HddDrives is { Count: > 0 });
    public bool? CompileNewGames
    {
        get => S.CompileNewGames;
        set
        {
            if (value is not { } v || v == S.CompileNewGames) return;
            Save(S with { CompileNewGames = v });
            Host.NewShadersChanged();
        }
    }
    public bool ShowsCloseQuits => Host.Platform.Tray;
    public bool? CloseQuits { get => S.CloseQuits; set { if (value is { } v && v != S.CloseQuits) Save(S with { CloseQuits = v }); } }
    public string CloseQuitsNote => Host.Tray.Shown
        ? "正在运行的编译将先安全保存缓存后退出。关闭后：点击窗口关闭按钮将最小化至托盘。右键点击托盘图标可彻底退出。"
        : "当前桌面环境无系统托盘，关闭窗口将直接退出程序。";

    /// <summary>While the page is shown: the close note follows the tray host as it comes and goes.</summary>
    public void Watch(bool on)
    {
        Host.Tray.Changed -= TrayChanged;
        if (!on) return;
        Host.Tray.Changed += TrayChanged;
        TrayChanged();
    }

    void TrayChanged() => Changed(nameof(CloseQuitsNote));

    public bool HasIntelGpu { get; } = Host.Core.HasIntelGpu;
    public bool? ExperimentalIntel { get => S.ExperimentalIntel; set { if (value is { } v && v != S.ExperimentalIntel) Save(S with { ExperimentalIntel = v }); } }
    // The GPU picker, shown only with more than one GPU SCSKiller compiles for; the first entry is the default rule.
    public IReadOnlyList<GpuChoice> GpuChoices { get; } = Host.Core.GpuChoices;
    public bool HasGpuChoices => GpuChoices.Count > 1;
    public IReadOnlyList<string> GpuLabels => ["Automatic: the GPU with the most video memory", .. GpuChoices.Select(c => c.Name)];
    public int GpuIndex
    {
        get => S.Gpu is not { } c ? 0 : GpuChoices.ToList() is var all && all.IndexOf(c) is >= 0 and var at ? at + 1
            : all.FindIndex(g => (g.Vendor, g.DeviceId, g.Name) == (c.Vendor, c.DeviceId, c.Name)) + 1;   // as GpuBackends.Chosen: 0 when gone
        set
        {
            if (value < 0 || value == GpuIndex) return;
            Save(S with { Gpu = value == 0 ? null : GpuChoices[value - 1] });
        }
    }

    public IReadOnlyList<string> Themes { get; } = [Host.Platform.SystemTheme, "浅色", "深色"];   // AppTheme.System..Dark
    public int ThemeIndex
    {
        get => (int)(S.Theme ?? AppTheme.System);
        set
        {
            if (value < 0 || value == ThemeIndex) return;
            Save(S with { Theme = value == 0 ? null : (AppTheme)value });
            Host.ApplyTheme(S.Theme);
        }
    }

    public double Threads { get => UsedThreads; set { if ((int)value != UsedThreads) Save(S with { Threads = (int)value }); } }
    static int UsedThreads => ScsKiller.UsedThreads(S.Threads);
    public string ThreadsText => $"{UsedThreads} of {Environment.ProcessorCount}";
    /// <summary>0 = Auto; 1 means 2 (the smallest budget offered).</summary>
    public double MaxCompileMemory { get => ShownMemory; set { var v = (int)value == 1 ? 2 : (int)value; if ((int)value != ShownMemory && v != S.MaxCompileMemoryGB) Save(S with { MaxCompileMemoryGB = v }); } }
    int ShownMemory => Math.Min(S.MaxCompileMemoryGB, (int)MaxCompileMemoryMax);
    public string MaxCompileMemoryNote => $"Auto picks the best limit for this PC's memory ({ScsKiller.AutoCompileMemoryGB()} GB here, with {ScsKiller.RamGB()} GB of RAM). Set it lower to keep more memory free while compiling, higher to compile faster.";
    public double MaxCompileMemoryMax => ScsKiller.MaxCompileMemoryGB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    public string MaxCompileMemoryText => S.MaxCompileMemoryGB > 0 ? $"{S.MaxCompileMemoryGB} GB" : $"Auto ({ScsKiller.AutoCompileMemoryGB()} GB)";
    public string PriorityNote => (S.Priority == WarmPriority.BelowNormal ? "低于正常优先级。" : "空闲优先级。") + " 较少线程 = 编译稍慢，电脑更安静。";

    /// <summary>A Yes/No question before an admin-only change: title, text, the Yes button's text.</summary>
    public delegate Task<bool> Confirm(string title, string text, string yes);

    /// <summary>Runs an admin-only action: in this process when it is already elevated or shows the design data, else the
    /// bundled CLI with these arguments behind a UAC prompt (<see cref="Elevated.Run"/>). Null = the user cancelled the
    /// prompt. Tests replace it.</summary>
    public static Func<Func<string>, Func<string[]>, Elevated.Result?> AsAdmin { get; set; } = (inProcess, cli) =>
        Host.Design == null && !Elevated.IsAdmin ? Elevated.Run(cli()) : new(true, inProcess());

    // an exception is a failure like the CLI's
    static Task<Elevated.Result?> AsAdminAsync(Func<string> inProcess, Func<string[]> cli) => Task.Run(() =>
    {
        try { return AsAdmin(inProcess, cli); }
        catch (Exception ex) { return new Elevated.Result(false, ex.Message); }
    });

    // the elevated CLI acts on the adapter this process runs on, not on a GPU picked since
    static string[] RunningGpu => [ScsKiller.AdapterArg, V.Gpu.AdapterLuid.ToString("X16")];

    /// <summary>Keeps Windows' automatic disk cleanup from clearing compiled shaders, or allows it again, behind one UAC
    /// prompt. A design window writes nothing. Null = the user cancelled the prompt.</summary>
    public static Task<Elevated.Result?> KeepShadersAsync(bool keep) =>
        AsAdminAsync(() => Host.Design != null ? "设计时示例数据：未做更改。" : D3DSCache.KeepFromCleanup(keep), () => ["shader-cleanup", keep ? "keep" : "allow"]);

    public bool ShowsShaderCleanup => D3DSCache.CleanupKept() != null;
    public string ShaderCleanupAction => D3DSCache.CleanupKept() == true ? "恢复允许 Windows 自动清理" : "阻止 Windows 自动清理已编译着色器";
    public bool CanApplyShaderCleanup { get; private set; } = true;
    public string? ShaderCleanupMessage { get; set; }
    public InfoBarSeverity ShaderCleanupSeverity { get; set; }
    public bool HasShaderCleanupMessage => ShaderCleanupMessage != null;

    public async Task ApplyShaderCleanupAsync()
    {
        CanApplyShaderCleanup = false;
        Changed();
        (ShaderCleanupMessage, ShaderCleanupSeverity) = await KeepShadersAsync(D3DSCache.CleanupKept() != true) switch
        {
            null => ("已取消。", InfoBarSeverity.Informational),
            { Ok: true } r => (r.Message, InfoBarSeverity.Success),
            var r => ("未作更改: " + r.Message, InfoBarSeverity.Error),
        };
        CanApplyShaderCleanup = true;
        Changed();
    }

    public bool CanApplyLimit { get; private set; } = true;
    public bool CanApplyAuto { get; private set; } = true;

    /// <summary>The cache limit's Apply: a global driver setting that needs admin, only after the user's explicit OK.</summary>
    public async Task ApplyLimitAsync(Confirm confirm)
    {
        var (label, limit) = Sizes[SizeIndex];
        if (!await confirm($"Set the shader cache limit to {label}?",
                $"This is a global {Fmt.Vendor(V.Vendor)} driver setting: it applies to every game on this PC, not only the ones SCSKiller compiles, " +
                "and stays until you change it. Windows will ask for administrator permission.\n\n" +
                "调大上限不会删除任何内容。若调低至低于当前使用量，驱动将清理旧条目，这些游戏会再次出现卡顿。 " +
                "您可以随时在此处切换回显卡驱动的默认值。",
                "修改上限")) return;

        CanApplyLimit = false;
        Changed();
        var size = limit.IsDriverDefault ? "default" : limit.Bytes is { } b ? (b / (double)(1L << 30)).ToString(System.Globalization.CultureInfo.InvariantCulture) : "unlimited";
        (ApplyMessage, ApplySeverity) = await AsAdminAsync(() => { V.SetCacheLimit(limit); return ""; }, () => ["cache", "set", size, .. RunningGpu]) switch
        {
            null => ("已取消。", InfoBarSeverity.Informational),
            { Ok: true } => ($"着色器缓存上限已设置为 {label}。", InfoBarSeverity.Success),
            var r => ("上限未更改: " + r.Message, InfoBarSeverity.Error),
        };
        CanApplyLimit = true;
        Refresh();
    }

    /// <summary>Auto Shader Compilation's Apply: a global driver setting + the driver's idle task, admin, only after the user's explicit OK.</summary>
    public async Task ApplyAutoAsync(Confirm confirm)
    {
        var level = AutoChoice;
        CanApplyAuto = false;   // before the first await: one apply at a time
        Changed();
        try
        {
            // Already so (e.g. the NVIDIA App set it): no question, no UAC prompt, nothing written.
            AutoShaderState? now = null;
            try { now = await Task.Run(V.GetAutoShaderCompilation); }
            catch (Exception) { }   // unknown: the apply reads it again and reports its own failure
            if (now != null && now.Is(level))
            {
                (AutoMessage, AutoSeverity) = (NvidiaBackend.AutoShaderOutcome(level, changed: false), InfoBarSeverity.Success);
                return;
            }
            if (!await confirm(level == AutoShaderCompilation.Off ? "关闭 NVIDIA 自动着色器编译？" : $"Turn on NVIDIA Auto Shader Compilation ({level})?",
                    "Auto Shader Compilation is a beta feature by NVIDIA®.", level == AutoShaderCompilation.Off ? "关闭" : "开启")) return;

            // --for-user: the elevated CLI refuses when UAC elevated another administrator (NvOSC would register the task for them).
            var result = await AsAdminAsync(() => V.SetAutoShaderCompilation(level),
                () => ["nvidia-auto-shader", level.ToString().ToLowerInvariant(), Elevated.ForUserArg, Elevated.CurrentUserSid, .. RunningGpu]);
            if (result is { Ok: false } && Host.Core is ScsKiller k) k.Log?.Report($"Auto Shader Compilation {level}: {result.Message}");
            (AutoMessage, AutoSeverity) = result switch
            {
                null => ("已取消。", InfoBarSeverity.Informational),
                { Ok: true } r => (r.Message is { Length: > 0 } m ? m : NvidiaBackend.AutoShaderOutcome(level, changed: true), InfoBarSeverity.Success),
                var r => ("应用失败: " + r.Message.Split('\n')[0], InfoBarSeverity.Error),   // NvOSC's full output goes to the log only
            };
        }
        finally
        {
            CanApplyAuto = true;
            Refresh();   // re-reads the setting and the task
        }
    }
}

/// <summary>A drive of Settings' per-drive HDD mode.</summary>
public sealed class HddDriveRow(string drive, bool on, Action<string, bool> set)
{
    public string Drive => drive;
    public bool? On { get => on; set { if (value is { } v && v != on) set(drive, v); } }
}
