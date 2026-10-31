using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SCSKiller.Core;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

/// <summary>A game added by hand: the exe the recorder goes next to, and the game folder the user confirms (or changes),
/// which SCSKiller checks whole for anti-cheat before it records.</summary>
static class GameFolderDialog
{
    /// <summary>The folder confirmed; null = cancelled.</summary>
    public static async Task<string?> ShowAsync(Game game, string title, string primary)
    {
        var folder = game.InstallDir;
        var folderText = new SelectableTextBlock { Text = folder, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var problem = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsVisible = false };
        var change = new Button { Content = "Change…", VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnSpacing = 12, ColumnDefinitions = new("*,Auto"), Children = { folderText, change } };
        Grid.SetColumn(change, 1);
        static TextBlock Label(string text) => new() { Text = text, Classes = { "BodyStrong" }, Margin = new(0, 8, 0, 0) };
        var dialog = new ContentDialog
        {
            Title = title, PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 4, MaxWidth = 520,
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "在开始录制前，SCSKiller 需要扫描整个游戏文件夹以检测反作弊组件。 "
                        + "请选择该游戏自身的独立文件夹，不要选择包含其他游戏的主目录。" },
                    Label("Game"),
                    new TextBlock { Text = game.Name, TextWrapping = TextWrapping.Wrap },
                    Label("游戏主程序（录制器将置于其旁）"),
                    new SelectableTextBlock { Text = game.ExePath, TextWrapping = TextWrapping.Wrap },
                    Label("游戏文件夹"),
                    row,
                    problem,
                },
            },
        };

        async Task Check()
        {
            folderText.Text = folder;
            var why = await Task.Run(() => Host.Core.ManualFolderProblem(game.ExePath, folder));   // lists the folder's subfolders
            problem.Message = why ?? "";
            problem.IsVisible = why != null;
            dialog.IsPrimaryButtonEnabled = why == null;
        }

        change.Click += async (_, _) =>
        {
            try
            {
                if ((await App.Main.StorageProvider.OpenFolderPickerAsync(new() { Title = "使用此文件夹" })).FirstOrDefault()?.TryGetLocalPath() is { } picked) folder = picked;
            }
            catch (Exception e)
            {
                problem.Message = "Couldn't open the folder picker: " + e.Message;
                problem.IsVisible = true;
                return;
            }
            await Check();
        };
        await Check();
        return await App.ShowAsync(dialog) == ContentDialogResult.Primary ? folder : null;
    }
}
