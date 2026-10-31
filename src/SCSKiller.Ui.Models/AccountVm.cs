using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Ui.Models;

/// <summary>Settings' Account card: a view over <see cref="Host.Account"/>, which changes on worker threads.</summary>
public sealed class AccountVm : Bindable
{
    static Account A => Host.Account!;
    readonly Coalesced changed;
    public AccountVm() => changed = new(Host.Ui, Changed);

    // The app sets Updater.Host once it runs the updater: until then, nothing reads the state that needs it.
    static bool Wired => Updater.Host.Core != null;

    /// <summary>While the page is shown. Signed in with nothing read yet this session: reads the status.</summary>
    public void Watch(bool on)
    {
        A.Changed -= changed.Request;
        Updater.Changed -= changed.Request;
        if (!on) return;
        A.Changed += changed.Request;
        Updater.Changed += changed.Request;
        Changed();
        if (A.SignedIn && A.Status == null && !A.Busy) _ = A.RefreshAsync();
    }

    // Failures show in the card (Account.Problem), so these never throw.
    public static void SignIn() => _ = A.SignInAsync();
    public static void CancelSignIn() => A.CancelSignIn();
    public static void RefreshStatus() => _ = A.RefreshAsync();
    public static void SignOut() { if (!Updater.Restarting) _ = A.SignOutAsync(); }

    /// <summary>"Go back to stable now": the latest stable, even though it's older than this pre-release (a downgrade, allowed this once).</summary>
    public static void BackToStable()
    {
        if (Updater.Restarting) return;   // the restart applies the update it waited for, or none
        Host.Core.Settings = Host.Core.Settings with { UpdateChannel = UpdateChannels.Stable };
        if (Wired) _ = Updater.CheckAsync(backToStable: true);
    }

    /// <summary>The post-sign-in share prompt (<see cref="OffersSharing"/>): asked once, ever, either answer dismisses it.</summary>
    public static void AnswerSharing(bool share) =>
        Host.Core.Settings = Host.Core.Settings with { SharePromptDismissed = true, ShareRecordings = share || Host.Core.Settings.ShareRecordings };

    public bool SignedIn => A.SignedIn;
    public bool SignedOut => !A.SignedIn;
    public bool SigningIn => A.SigningIn;
    public bool Busy => A.Busy;
    public bool Idle => !A.Busy;
    public string? Problem => A.Problem;
    public bool HasProblem => A.Problem != null;
    public bool OffersSharing => A.OffersSharing(Host.Core.Settings);
    public void Refresh() => Changed();   // the settings changed (OffersSharing)

    // "Download shared shader hashes": enabled only with "db" (follows sign-in, sign-out, refresh and a lapse through
    // Changed); the setting keeps its value. One quiet line under it: why it's off, or its last failure.
    public bool HasDb => A.HasDb;
    // Unticked without "db" whatever the setting says; the binding writes that false back, which must not reach the setting.
    public bool? UseCommunityDb
    {
        get => A.HasDb && Host.Core.Settings.UseCommunityDb;
        set
        {
            if (!A.HasDb || value is not { } v || v == Host.Core.Settings.UseCommunityDb) return;
            Host.Core.Settings = Host.Core.Settings with { UseCommunityDb = v };
            Changed();
        }
    }
    public string? DbNote => !A.SignedIn ? "收录游戏列表对所有用户开放查询。共享哈希数据需要 Patreon 赞助会员。"
        : A.Status is not { } st ? null   // not read yet: the status line above says "Checking…"
        : !A.HasDb ? "您当前的 Patreon 会员暂不包含社区数据库权益。此前已下载的数据可继续使用。"
        : (Host.Core as ScsKiller)?.Community?.Problem;
    public bool HasDbNote => DbNote != null;

    public string Benefits => A.Status switch
    {
        null => A.Busy ? "正在检查您的赞助者身份…" : "赞助者身份尚未检查。",
        { Benefits: { } b } => b,   // the flag names live in Core (AccountStatus) so they're testable
        _ => "当前暂无赞助者权益。Patreon 会员生效后即可享用：届时请点击“刷新状态”。",
    };

    // Update channel: a picker only when the token grants more than stable.
    static readonly Dictionary<string, string> ChannelNames = new()
    {
        [UpdateChannels.Stable] = "稳定正式版", [UpdateChannels.Beta] = "Beta 测试版（Patreon 赞助者）",
        [UpdateChannels.Alpha] = "Alpha 尝鲜版（Patreon 早期支持者）", [UpdateChannels.Internal] = "Internal",
    };
    static IReadOnlyList<string> Offered => UpdateChannels.Offered(A.Status?.Ent);
    static string Chosen => UpdateChannels.Effective(Host.Core.Settings.UpdateChannel, AppVersion.Current.Channel, A.Status?.Ent);
    List<string> labels = [];
    public List<string> ChannelLabels => labels.SequenceEqual(Offered.Select(c => ChannelNames[c])) ? labels : labels = Offered.Select(c => ChannelNames[c]).ToList();   // same list: the ComboBox keeps its selection
    // a Flatpak or a package: no channel to pick, and the note says where its updates come from
    static string? UpdatesFrom => Wired ? Updater.Installation.UpdatesFrom : null;
    bool Entitled => A.SignedIn && Offered.Count > 1;
    public bool ShowsChannels => Entitled && UpdatesFrom == null;
    public bool CanPickChannel => !Updater.Restarting;
    public bool CanSignOut => Idle && !Updater.Restarting;   // signing out can change the channel a restart applies
    public int ChannelIndex
    {
        get => Math.Max(0, Offered.ToList().IndexOf(Chosen));
        set
        {
            if (value < 0 || value >= Offered.Count || Offered[value] == Chosen || Updater.Restarting) return;   // -1: the list was replaced
            Host.Core.Settings = Host.Core.Settings with { UpdateChannel = Offered[value] };
            if (Wired) _ = Updater.CheckAsync();
            Changed();
        }
    }
    /// <summary>"Go back to stable now": on an installed pre-release build, whatever the account.</summary>
    public bool OffersBackToStable => Wired && Updater.Installed && AppVersion.Current.Channel != UpdateChannels.Stable;
    public bool ShowsUpdates => Entitled || OffersBackToStable || UpdatesFrom != null;
    public string ChannelNote => UpdatesFrom ?? (Updater.Checking ? "正在检查更新…"
        : (InstallsAutomatically ? "更新会在后台自动下载，并在 SCSKiller 下次启动或退出时安装，绝不打扰使用。"
            : "更新会在后台自动下载，点击“重启并更新”即可安装。") + " 切换离开尝鲜测试通道将保留当前版本，直至正式稳定版版本号超越该版本。");
        : Wired && Updater.Ready is { } v ? AutoInstall.ReadyNote(v, Updater.InstallsAutomatically)
        : (InstallsAutomatically ? "Updates download in the background and install the next time SCSKiller starts or quits, never during a compile or a game."
            : "Updates download in the background, and Restart to update installs them.") + " Leaving an early channel keeps this build until Stable passes it.");
    // unwired, nothing is downloaded: the next download's channel is the chosen one, as Updater.Channel says then
    static bool InstallsAutomatically => Wired ? Updater.InstallsAutomatically : AutoInstall.On(Host.Core.Settings, Chosen);
    public string? UpdateProblem => Updater.Problem;
    public bool HasUpdateProblem => Updater.Problem != null;
}
