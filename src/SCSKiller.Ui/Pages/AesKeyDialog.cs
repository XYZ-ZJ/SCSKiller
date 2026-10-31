using Avalonia.Controls;
using Avalonia.Media;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

/// <summary>The user's own AES key for an encrypted Unreal game, or a BlackSpace game's key: checked against the game's
/// files and stored on this PC only.</summary>
static class AesKeyDialog
{
    /// <summary>True once a key that opens the game's files is stored; the caller then re-reads the game.
    /// <paramref name="reason"/>: the Library's Why?, shown above the key.</summary>
    public static async Task<bool> ShowAsync(string id, string title, string? reason = null, bool aes = true)
    {
        var box = new TextBox { PlaceholderText = "以 0x 开头的 64 位十六进制密钥", Classes = { "clearButton" } };
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = (aes ? AesIntro : GameKeyIntro) +
                    " If you have the key, paste it here: it's checked against the game's files and kept on this PC only." },
                new StackPanel { Spacing = 8, Children = { new TextBlock { Text = aes ? "AES 密钥" : "游戏密钥" }, box } },
            },
        };
        if (reason != null) panel.Children.Insert(0, new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = title, Content = panel, CloseButtonText = "关闭", PrimaryButtonText = "解锁", DefaultButton = ContentDialogButton.Close };
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
        var key = (box.Text ?? "").Trim();
        string? problem = null;
        bool ok;
        try { ok = await Task.Run(() => Host.Core.SetEncryptionKey(id, key)); }   // opens the game's files
        catch (Exception ex) { (ok, problem) = (false, ex.Message); }
        if (!ok) await App.ShowAsync(new ContentDialog { Title = "该密钥无效", Content = problem ?? "它无法解密此游戏的文件。", CloseButtonText = "OK" });
        return ok;
    }

    const string AesIntro = "SCSKiller 无法在游戏的可执行文件中找到该密钥（受保护的 exe 会隐藏它）。";
    const string GameKeyIntro = "提供该游戏的密钥后，SCSKiller 无需录制即可直接进行编译。";
}
