using System.Windows;
using System.Windows.Controls;
using GameShelf.Models;
using GameShelf.Services;

namespace GameShelf.Views;

/// <summary>One row of the folder list.</summary>
internal sealed record FolderChoice(InstallFolder Folder, long RequiredBytes)
{
    public string Title => string.IsNullOrWhiteSpace(Folder.Label) ? Folder.Drive : $"{Folder.Label} ({Folder.Drive})";

    public string FreeText => Loc.T("{0} free", DisplayFormat.Size(Folder.FreeBytes));

    /// <summary>Room for the download with a 10% margin (Steam needs temporary space while installing).</summary>
    public bool HasSpace => Folder.FreeBytes >= RequiredBytes + RequiredBytes / 10;
}

/// <summary>
/// GameShelf's own install window: where to install and which shortcuts to create. The installation itself
/// is then started in Steam (see <see cref="Steam.SteamInstaller"/>).
/// </summary>
internal sealed partial class InstallDialog : UserControl
{
    const string LastFolderSetting = "install-folder.txt";

    /// <summary>The user's choices, or null if the dialog was cancelled.</summary>
    public InstallRequest? Request { get; private set; }

    public InstallDialog(Game game, InstallPlan plan)
    {
        InitializeComponent();
        Loc.Apply(this);

        TitleText.Text = Loc.T("Install {0}", game.Name);
        SizeText.Text = plan.RequiredBytes > 0 ? Loc.T("Download size: {0}", DisplayFormat.Size(plan.RequiredBytes)) : "";

        var choices = plan.Folders.Select(folder => new FolderChoice(folder, plan.RequiredBytes)).ToList();
        FolderList.ItemsSource = choices;
        FolderList.SelectedItem = ChooseDefault(choices);
        SpaceWarning.Visibility = choices.Any(c => c.HasSpace) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The folder used last time, else Steam's default, else the first one with enough room.</summary>
    static FolderChoice? ChooseDefault(IReadOnlyList<FolderChoice> choices)
    {
        var withSpace = choices.Where(c => c.HasSpace).ToList();
        var lastPath = AppData.ReadText(LastFolderSetting)?.Trim();
        return withSpace.FirstOrDefault(c => c.Folder.Path == lastPath)
            ?? withSpace.FirstOrDefault(c => c.Folder.IsDefault)
            ?? withSpace.FirstOrDefault();
    }

    void OnFolderSelected(object sender, SelectionChangedEventArgs e) =>
        InstallButton.IsEnabled = FolderList.SelectedItem is not null;

    void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not FolderChoice choice) return;
        Request = new InstallRequest(choice.Folder.Index, DesktopCheck.IsChecked == true, StartMenuCheck.IsChecked == true);
        AppData.WriteText(LastFolderSetting, choice.Folder.Path);
        DialogHost.Close(this);
    }

    void OnCancelClick(object sender, RoutedEventArgs e) => DialogHost.Close(this);
}
