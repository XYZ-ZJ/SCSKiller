using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Ui.Models;

/// <summary>A cache folder on the Cache page.</summary>
public sealed record CacheFolderRow(string Label, string Path, string Size, string? Note)
{
    public bool HasNote => Note != null;
}

/// <summary>A cache item; Folders: where its files are (<see cref="CacheInventory.FoldersOf"/>).</summary>
public sealed record CacheItemRow(string Name, string Size, IReadOnlyList<string> Folders, string? Note, CacheItem Item, CacheGroupKind Kind, bool Enabled, object? Icon = null)
{
    public bool NoIcon => Icon == null;
    public bool HasFolder => Folders.Count > 0;
    public bool HasNote => Note != null;
}

public sealed record CacheGroupRow(string Title, string Size, string Caption, string? Note, IReadOnlyList<CacheItemRow> Items, bool Expanded,
    CacheGroup Group, bool Enabled)
{
    public bool HasNote => Note != null;
}

/// <summary>The Cache page: a read of <see cref="IScsKiller.CacheInventory"/> off the UI thread, and its deletes.</summary>
public sealed class CacheVm : Bindable
{
    CacheInventory? read;
    HashSet<string> games = [];   // the library the last read attributed by
    string split = "";
    readonly BackgroundRead<(CacheInventory Inventory, CacheUsage Usage, CacheLimit? Limit, long? Capped, long? Fullest)> cache;

    public CacheVm()
    {
        cache = new("Cache page", () => (Host.Core.CacheInventory(), Host.Core.Vendor.GetCacheUsage(), Host.Core.Vendor.GetCacheLimit(), (Host.Core.Vendor as AmdBackend)?.AppCache.DxcBytes(), LibraryVm.Fullest()),
            r =>
            {
                read = r.Inventory;
                split = Format.CacheSplit(r.Usage, r.Limit, r.Capped, Host.Core.WindowsCacheBytes);
                UpperNote = Format.CacheUpperNote(r.Usage);
                var fullest = r.Limit?.Bytes != null ? r.Fullest : null;
                Warning = Format.CacheWarning(Host.Core.Vendor.Vendor, r.Limit, fullest ?? r.Capped ?? r.Usage.BytesOnDisk, fullest != null);
                Changed();
            });
        Refresh();
    }

    public void Refresh()
    {
        games = Host.Core.Games.Select(g => g.Game.Id).ToHashSet();
        cache.Request();
    }

    /// <summary>A scan that lists other games (the first one after start among them) moves caches between groups.</summary>
    public void GamesChanged()
    {
        if (!games.SetEquals(Host.Core.Games.Select(g => g.Game.Id))) Refresh();
    }

    public CacheInventory? Inventory => read;
    public string Total => read == null ? "正在读取缓存…" : split;
    public string? UpperNote { get; private set; }
    public string? Warning { get; private set; }
    public bool HasWarning => Warning != null;
    public bool NoFolders => read is { Folders.Count: 0, Groups.Count: 0 };

    public bool Deleting { get; private set; }
    public bool CanDelete => read != null && !Deleting;
    public bool CanClearEverything => CanDelete && read!.Everything(withOthers: true).Count > 0;
    public string? Result { get; private set; }
    public bool HasResult => Result != null;
    public InfoBarSeverity ResultSeverity { get; private set; }

    /// <summary>Deletes off the UI thread, then reads the caches again: the page shows what is left, not a subtraction.</summary>
    public async Task DeleteAsync(IReadOnlyList<CacheItem> items)
    {
        (Deleting, Result) = (true, null);
        Changed();
        try
        {
            var r = await Task.Run(() => Host.Core.DeleteCaches(items));
            Result = $"已删除 {r.Files} 个文件中的 {Format.Bytes(r.Bytes)} 缓存。"
                + (r.Skipped.Count > 0 ? $" Skipped {r.Skipped.Count}:\n" + string.Join("\n", r.Skipped) : "");
            ResultSeverity = r.Skipped.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        }
        catch (Exception e)
        {
            (Result, ResultSeverity) = ($"未删除任何内容: {e.Message}", InfoBarSeverity.Error);
        }
        Deleting = false;
        Refresh();
        Changed();
    }

    long OldDriver => read?.Bytes(CacheGroupKind.OldDriver) ?? 0;
    public bool HasOldDriver => OldDriver > 0;
    public string OldDriverText => $"有 {Format.Bytes(OldDriver)} 的缓存文件来自当前显卡驱动安装前（{read?.DriverInstalled?.ToLocalTime():d}）。安装新驱动后从未再次使用。";
    public string FreeText => $"释放 {Format.Bytes(OldDriver)}";
    public IReadOnlyList<CacheItem> OldDriverItems => read?.Groups.Where(g => g.Kind == CacheGroupKind.OldDriver).SelectMany(g => g.Items).ToList() ?? [];

    public IReadOnlyList<CacheFolderRow> Folders => read?.Folders.Select(f => new CacheFolderRow(f.Label, f.Path,
        f.Cap is { } cap ? $"{Format.Bytes(f.Bytes)} of {Format.Bytes(cap)}" : Format.Bytes(f.Bytes),
        f.TargetMissing ? "此文件夹为软链接，但目标路径不存在"
        : f.Cap is { } full && f.Bytes >= full * 9 / 10 ? "接近饱和：驱动会在游戏启动时自动清除最久未运行游戏的缓存"
        : f.Final != null ? $"通过链接存储于 {f.Final}"
        : !f.Exists ? "尚未创建" : null)).ToList() ?? [];

    public IReadOnlyList<CacheGroupRow> Groups => read?.Groups.Select(g =>
    {
        var (title, caption) = Texts(g.Kind, read.DriverInstalled);
        var items = g.Items.Select(i => new CacheItemRow(ItemName(g.Kind, i), Format.OnDisk(i.Bytes, i.InUse), read.FoldersOf(i), i.Note, i, g.Kind, CanDelete && !i.Kept, g.Kind == CacheGroupKind.Installed ? GameIcon(i) : null)).ToList();
        return new CacheGroupRow(title, Format.OnDisk(g.Bytes, g.InUse), caption, g.Note, items, g.Kind != CacheGroupKind.Unattributed, g, CanDelete && g.Items.Any(i => !i.Kept));
    }).ToList() ?? [];

    // The Library's icon source and cache, so no new extraction path.
    static object? GameIcon(CacheItem i) =>
        i.GameIds.Select(id => Host.Core.Games.FirstOrDefault(g => g.Game.Id == id)).FirstOrDefault(g => g != null) is { } game ? Host.Icons.Get(game.Game.ExePath) : null;

    /// <summary>An icon finished loading: the rows read it again.</summary>
    public void IconsChanged() => Changed();

    static string ItemName(CacheGroupKind kind, CacheItem i) => kind == CacheGroupKind.Unattributed && i.Name.Length == 8 && i.Name.All(char.IsAsciiHexDigit) ? $"Unknown app {i.Name}" : i.Name;

    public static (string Title, string Caption) Texts(CacheGroupKind kind, DateTime? installed) => kind switch
    {
        CacheGroupKind.Installed => ("已安装的游戏", "根据 SCSKiller 识别出的缓存键归类的本地游戏。"),
        CacheGroupKind.Uninstalled => ("已卸载的游戏", "SCSKiller 编译过或记录过运行、但当前未在游戏库中的游戏，以可执行文件名标识。"),
        CacheGroupKind.OldDriver => ("旧驱动残留", $"在当前驱动安装前（{installed?.ToLocalTime():d}）写入的缓存。安装新驱动后从未再次使用。"),
        CacheGroupKind.OldBuild => ("早期编译残留", "由早期编译留下的缓存，游戏不会读取它们。"),
        CacheGroupKind.OtherVendor => ("其他 GPU 厂商缓存", "其他品牌显卡遗留的缓存文件。"),
        _ => ("其他应用与游戏", "桌面应用程序，以及 SCSKiller 尚未编译或记录过运行的游戏。一旦检测到运行，将自动归入对应分类。"),
    };

    /// <summary>The confirm's text for deleting <paramref name="items"/> of one group: what happens after.</summary>
    public static string Consequence(CacheGroupKind kind, IReadOnlyList<CacheItem> items) => kind switch
    {
        CacheGroupKind.Installed or CacheGroupKind.Uninstalled =>
            $"These games compile their shaders again on their next compile, and stutter until then: {string.Join(", ", items.Select(i => i.Name))}.",
        CacheGroupKind.OldDriver => "当前驱动安装以来从未读取过这些文件。无需重新编译任何内容。",
        CacheGroupKind.OldBuild => "游戏不会读取这些文件。",
        CacheGroupKind.OtherVendor => "在该 GPU 上运行的应用程序将在运行时重新构建其缓存。",
        _ => "这些应用和游戏将在运行时重新构建缓存，在此之前可能会出现短暂卡顿。",
    } + string.Concat(items.Select(i => i.Note).OfType<string>().Select(n => " " + n)) + " 正在被占用的文件将自动保留跳过。";
}
