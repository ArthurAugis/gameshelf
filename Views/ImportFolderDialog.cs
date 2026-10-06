using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameShelf.Launchers;
using GameShelf.Services;
using Microsoft.Win32;

namespace GameShelf.Views;

/// <summary>
/// Adds the games of a folder at once: one entry per game, its console guessed and its cover searched by name. Pick the
/// folder (and the emulator, for ROMs and ISOs), look at what was found, untick what is wrong, import.
/// </summary>
internal sealed class ImportFolderDialog : StackPanel
{
    const int ParallelCoverSearches = 4;

    readonly TextBox folder = AddGameDialog.Field(), emulator = AddGameDialog.Field();
    readonly TextBlock status = new() { Foreground = AddGameDialog.Muted, FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock error = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0x73, 0x5f)), FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly StackPanel list = new() { Margin = new Thickness(0, 6, 8, 0) };
    readonly List<(CheckBox Box, FoundGame Game)> rows = new();
    readonly Button scan, import, cancel;
    bool busy;

    ImportFolderDialog(string? startFolder)
    {
        Width = 640;
        Margin = new Thickness(30, 26, 30, 26);
        folder.Text = startFolder ?? "";

        Children.Add(new TextBlock { Text = Loc.T("Import games from a folder"), FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        Children.Add(new TextBlock
        {
            Text = Loc.T("PC games: one game per sub-folder, GameShelf picks its program. ROMs and ISOs: pick the emulator that runs them, and GameShelf writes the arguments and guesses the console."),
            Foreground = AddGameDialog.Muted,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var browseFolder = AddGameDialog.Button(Loc.T("Browse..."), secondary: true, 100);
        browseFolder.Click += async (_, _) => await PickFolderAsync();
        Children.Add(AddGameDialog.Row(Loc.T("Folder"), folder, browseFolder));

        var browseEmulator = AddGameDialog.Button(Loc.T("Browse..."), secondary: true, 100);
        browseEmulator.Click += async (_, _) => await PickEmulatorAsync();
        Children.Add(AddGameDialog.Row(Loc.T("Emulator (leave empty for PC games)"), emulator, browseEmulator));

        scan = AddGameDialog.Button(Loc.T("Scan"), secondary: true, 110);
        scan.Margin = new Thickness(0, 16, 0, 0);
        scan.HorizontalAlignment = HorizontalAlignment.Left;
        scan.Click += async (_, _) => await ScanAsync();
        Children.Add(scan);
        Children.Add(status);
        Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Children.Add(error);

        cancel = AddGameDialog.Button(Loc.T("Cancel"), secondary: true, 110);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 10, 0);
        cancel.Click += (_, _) => DialogHost.Close(this);
        import = AddGameDialog.Button(Loc.T("Import"), secondary: false, 150);
        import.IsDefault = true;
        import.IsEnabled = false;
        import.Click += async (_, _) => await ImportAsync();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        footer.Children.Add(cancel);
        footer.Children.Add(import);
        Children.Add(footer);

        if (startFolder is not null) Loaded += async (_, _) => await ScanAsync();
    }

    /// <summary>Opens the window, with <paramref name="startFolder"/> already scanned when given.</summary>
    public static void Show(Window owner, string? startFolder = null) => DialogHost.ShowModal(owner, new ImportFolderDialog(startFolder));

    async Task PickFolderAsync()
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() != true) return;
        folder.Text = dialog.FolderName;
        await ScanAsync();
    }

    async Task PickEmulatorAsync()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Programs") + "|*.exe;*.lnk;*.bat;*.cmd|" + Loc.T("All files") + "|*.*" };
        if (dialog.ShowDialog() != true) return;
        emulator.Text = dialog.FileName;
        if (Directory.Exists(folder.Text.Trim().Trim('"'))) await ScanAsync();
    }

    async Task ScanAsync()
    {
        if (busy) return;
        var path = folder.Text.Trim().Trim('"');
        var program = emulator.Text.Trim().Trim('"');
        if (!Directory.Exists(path))
        {
            ShowError(Loc.T("Pick the folder to import."));
            return;
        }
        if (program.Length > 0 && !File.Exists(program))
        {
            ShowError(Loc.T("Pick the program that starts the game."));
            return;
        }

        error.Visibility = Visibility.Collapsed;
        SetBusy(true);
        status.Text = Loc.T("Scanning...");
        list.Children.Clear();
        rows.Clear();

        var result = await Task.Run(() => FolderScan.Scan(path, program.Length == 0 ? null : program));
        var known = ManualGames.KnownLaunches();
        var fresh = result.Games.Where(g => !known.Contains(ManualGames.LaunchKey(g.Exe, g.Arguments))).ToList();

        foreach (var game in fresh) AddRow(game);
        var notes = new List<string>
        {
            fresh.Count == 0 ? Loc.T("No new game found in this folder.") : Loc.Count(fresh.Count, "{0} game found.", "{0} games found."),
        };
        if (result.Games.Count > fresh.Count) notes.Add(Loc.T("{0} already on the shelf.", result.Games.Count - fresh.Count));
        if (result.RomsWithoutEmulator > 0) notes.Add(Loc.Count(result.RomsWithoutEmulator, "{0} ROM file needs an emulator: pick one above.", "{0} ROM files need an emulator: pick one above."));
        status.Text = string.Join(" ", notes);
        SetBusy(false);
        UpdateImportButton();
    }

    void AddRow(FoundGame game)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = game.Name, Foreground = Brushes.White, FontSize = 13.5 });
        line.Children.Add(new TextBlock { Text = game.Console, Foreground = AddGameDialog.Muted, FontSize = 12, Margin = new Thickness(10, 1, 0, 0) });
        var box = new CheckBox { IsChecked = true, Content = line, Margin = new Thickness(0, 3, 0, 3), ToolTip = game.Exe };
        box.Click += (_, _) => UpdateImportButton();
        rows.Add((box, game));
        list.Children.Add(box);
    }

    List<FoundGame> Chosen() => rows.Where(r => r.Box.IsChecked == true).Select(r => r.Game).ToList();

    void UpdateImportButton()
    {
        int count = Chosen().Count;
        import.Content = count == 0 ? Loc.T("Import") : Loc.Count(count, "Import {0} game", "Import {0} games");
        import.IsEnabled = count > 0 && !busy;
    }

    void SetBusy(bool value)
    {
        busy = value;
        scan.IsEnabled = !value;
        cancel.IsEnabled = !value; // the dialog stays until the covers are found and the games are added
        import.IsEnabled = !value && Chosen().Count > 0;
    }

    /// <summary>Looks for the covers a few at a time, then draws the missing ones and adds all the games in one go.</summary>
    async Task ImportAsync()
    {
        var chosen = Chosen();
        if (chosen.Count == 0 || busy) return;
        SetBusy(true);

        int done = 0;
        IProgress<int> progress = new Progress<int>(n => status.Text = Loc.T("Importing {0} / {1}...", n, chosen.Count));
        progress.Report(0);
        var pictures = new byte[]?[chosen.Count];
        using var gate = new SemaphoreSlim(ParallelCoverSearches);
        await Task.WhenAll(chosen.Select(async (game, i) =>
        {
            await gate.WaitAsync();
            try
            {
                var console = CoverSearch.Consoles.FirstOrDefault(c => c.Label == game.Console) ?? CoverSearch.Consoles[0];
                pictures[i] = await CoverSearch.BestAsync(game.Name, console);
            }
            finally
            {
                gate.Release();
                progress.Report(Interlocked.Increment(ref done));
            }
        }));

        // Drawing a cover needs this thread.
        ManualGames.AddMany(chosen.Select((game, i) =>
            (game.Name, game.Exe, game.Arguments, game.Console,
                pictures[i] is { } picture ? CoverImage.FromPicture(picture) : CoverImage.Generate(game.Name, game.Exe))).ToList());
        DialogHost.Close(this);
    }

    void ShowError(string text)
    {
        error.Text = text;
        error.Visibility = Visibility.Visible;
    }
}
