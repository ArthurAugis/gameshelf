using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GameShelf.Controls;
using GameShelf.Launchers;
using GameShelf.Models;
using GameShelf.Services;
using GameShelf.Steam;
using GameShelf.Theming;

namespace GameShelf.Views;

/// <summary>
/// Details of one game: 3D box, logo, store description, key facts, and the play / install / uninstall / hide
/// actions. Installing shows live progress; the window keeps itself in sync with what Steam does.
/// </summary>
internal sealed partial class DetailView : UserControl
{
    const int MaxDescriptionLength = 340;
    const int MaxTitleLength = 90, LongTitleLength = 40; // a title longer than this is cut, or shown smaller
    static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(1500);
    static readonly TimeSpan StartGracePeriod = TimeSpan.FromSeconds(20);

    readonly Game game;
    readonly GameBoxView box;
    readonly DispatcherTimer syncTimer = new() { Interval = SyncInterval };

    PlayStatus status;           // the user's own status and rating for this game
    int rating;
    bool closed;
    bool syncing;
    bool watchProgress;          // poll the download queue (only while an install is running or just started)
    bool installing;             // the progress panel is showing
    bool epicPaused;             // an Epic download GameShelf paused (the launcher does not say)
    bool epicStopped;            // an Epic game left partly downloaded: to finish or to delete
    readonly Queue<(DateTime At, long Bytes)> epicSamples = new(); // size of the Epic install folder over time: the launcher gives no speed
    bool paused;
    long installTotalBytes;      // download size, known when this window started the install
    Task<InstallPlan?>? plan;    // size and folders, asked when the window opened
    DateTime planAskedAt;
    DateTime? installStartedAt;  // right after Install, Steam's queue may not list the game yet

    public DetailView(Game game, Color color)
    {
        InitializeComponent();
        Loc.Apply(this);
        this.game = game;

        Glow.Fill = new RadialGradientBrush(ColorUtil.Shade(color, 1.6), Colors.Transparent);
        box = new GameBoxView(game, color);
        Stage.Children.Add(box);

        ShowIdentity();
        BuildCollectionChips();
        ShowNote();
        ApplyInstallState();

        syncTimer.Tick += async (_, _) => await SyncAsync();
        Gamepad.Pressed += OnGamepadPressed;
        // Already fetched by the shelf's prefetch in most cases: this then completes before the page is drawn.
        _ = ShowOnlineInfoAsync();
        Loaded += async (_, _) =>
        {
            Focus(); // so Escape reaches this page, not the shelf behind it
            syncTimer.Start();
            await DetectRunningInstallAsync();
            // Ready before the user clicks Install, so the disk choice opens at once.
            if (!game.Installed && !installing && game.Launcher == Launcher.Steam) PreparePlan();
        };
    }

    /// <summary>The user chose another launcher of the same title: the shelf opens that game's page instead.</summary>
    public event Action<Game>? SwitchRequested;

    /// <summary>Raised when the page is closed (Back, Escape, B, or after hiding the game).</summary>
    public event Action? Closed;

    /// <summary>The window that shows this page: the owner of the dialogs it opens.</summary>
    Window Host => Window.GetWindow(this)!;

    /// <summary>Stops the page's background work, saves the note and tells the shelf to remove the page.</summary>
    public void Close()
    {
        if (closed) return;
        closed = true;
        syncTimer.Stop();
        Gamepad.Pressed -= OnGamepadPressed;
        SaveNote();
        Closed?.Invoke();
    }

    void OnBackClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Asks Steam for the download size and the folders, in the background.</summary>
    void PreparePlan()
    {
        plan = SteamInstaller.PrepareAsync(game.AppId);
        planAskedAt = DateTime.UtcNow;
    }

    // ---- What is shown ----

    /// <summary>Logo or title, play time: known instantly, from files on this PC.</summary>
    void ShowIdentity()
    {
        if (game.Logo is { } logoPath)
        {
            LogoImage.Source = ImageLoader.Load(logoPath, 600);
            LogoImage.ToolTip = game.Name;
            LogoImage.Visibility = Visibility.Visible;
            TitleText.Visibility = Visibility.Collapsed;
        }
        else
        {
            TitleText.Text = DisplayFormat.Shorten(game.Name, MaxTitleLength);
            TitleText.ToolTip = game.Name;
            if (game.Name.Length > LongTitleLength) TitleText.FontSize = 26;
        }
        DescriptionText.Text = Loc.T("Loading details...");

        // Steam and GOG Galaxy record play time; Epic's launcher keeps it out of reach, so it is unknown there.
        var stats = game.Launcher switch
        {
            Launcher.Steam => SteamLibrary.ReadPlayStats(game.AppId),
            Launcher.Gog or Launcher.Manual => new PlayStats(game.PlayTime, game.LastPlayed),
            _ => null,
        };
        PlayTimeValue.Text = stats is { PlayTime.TotalMinutes: > 0 } ? DisplayFormat.PlayTime(stats.PlayTime)
            : game.Launcher is Launcher.Steam or Launcher.Gog or Launcher.Manual ? Loc.T("Never played") : "-";
        LastPlayedValue.Text = stats?.LastPlayed is { } date ? date.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "-";
        foreach (var launcher in game.OwnedOn) GenreChips.Children.Add(CreatePlatformChip(launcher));
        EditButton.Visibility = game.Launcher == Launcher.Manual ? Visibility.Visible : Visibility.Collapsed;
        HideButton.Content = HiddenGames.Contains(game.KeyId)
            ? Loc.T("Show this game on the shelf again")
            : Loc.T("Hide this game from the shelf");
    }

    /// <summary>One toggle per collection: lit when the game is in it.</summary>
    void BuildCollectionChips()
    {
        CollectionChips.Children.Clear();
        foreach (var name in GameCollections.Names)
        {
            var chip = new ToggleButton
            {
                Content = Loc.T(name),
                Style = (Style)FindResource("ChipToggle"),
                IsChecked = GameCollections.Contains(name, game.KeyId),
            };
            chip.Click += (_, _) => GameCollections.Toggle(name, game.KeyId);
            CollectionChips.Children.Add(chip);
        }
    }

    // ---- Personal status, rating and note ----

    void ShowNote()
    {
        var note = GameNotes.Get(game.KeyId);
        status = note.Status;
        rating = note.Rating;
        CommentBox.Text = note.Comment;
        CommentPlaceholder.Visibility = note.Comment.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        BuildStatusChips();
        BuildStars();
    }

    /// <summary>One chip per status; clicking the lit one clears it.</summary>
    void BuildStatusChips()
    {
        StatusChips.Children.Clear();
        foreach (var value in new[] { PlayStatus.Playing, PlayStatus.Finished, PlayStatus.Dropped })
        {
            var chip = new ToggleButton
            {
                Content = Loc.T(value.ToString()),
                Style = (Style)FindResource("ChipToggle"),
                IsChecked = status == value,
            };
            chip.Click += (_, _) =>
            {
                status = chip.IsChecked == true ? value : PlayStatus.None;
                SaveNote();
                BuildStatusChips();
            };
            StatusChips.Children.Add(chip);
        }
    }

    /// <summary>Five stars; clicking the last lit one clears the rating.</summary>
    void BuildStars()
    {
        Stars.Children.Clear();
        var gold = new SolidColorBrush(Color.FromRgb(0xd9, 0xb7, 0x7a));
        var grey = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255));
        for (int star = 1; star <= GameNotes.MaxRating; star++)
        {
            int value = star;
            var button = new Button
            {
                Content = value <= rating ? "" : "", // filled / outline star of Segoe MDL2 Assets
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 17,
                Foreground = value <= rating ? gold : grey,
                Style = (Style)FindResource("LinkButton"),
                Padding = new Thickness(4, 2, 4, 2),
                ToolTip = $"{value}/{GameNotes.MaxRating}",
            };
            button.Click += (_, _) =>
            {
                rating = rating == value ? 0 : value;
                SaveNote();
                BuildStars();
            };
            Stars.Children.Add(button);
        }
    }

    void SaveNote() => GameNotes.Set(game.KeyId, new GameNote(status, rating, CommentBox.Text.Trim()));

    void OnCommentChanged(object sender, TextChangedEventArgs e) =>
        CommentPlaceholder.Visibility = CommentBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

    void OnCommentLostFocus(object sender, RoutedEventArgs e) => SaveNote();

    // ---- Keyboard and controller ----

    /// <summary>Escape closes the page, unless the user is typing a note (then it just leaves the field).</summary>
    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (Keyboard.FocusedElement is TextBox) Keyboard.ClearFocus();
        else Close();
        e.Handled = true;
    }

    /// <summary>B closes the window, A does the main action (Play, or Install).</summary>
    void OnGamepadPressed(PadButton button)
    {
        if (Host is not { IsActive: true } || DialogHost.IsOpen) return;
        if (button == PadButton.B)
        {
            Close();
            return;
        }
        if (button != PadButton.A) return;

        var primary = PlayButton.Visibility == Visibility.Visible ? PlayButton
            : InstallButton.Visibility == Visibility.Visible ? InstallButton
            : null;
        primary?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    /// <summary>Status, storage and which buttons are available, from the installed / installing state.</summary>
    void ApplyInstallState()
    {
        var green = Color.FromRgb(0x5c, 0xc8, 0x4a);
        var amber = Color.FromRgb(0xe0, 0xa8, 0x3a);
        var grey = Color.FromRgb(0x6a, 0x66, 0x5e);
        StatusDot.Fill = new SolidColorBrush(installing ? amber : game.Installed ? green : grey);
        StatusValue.Text = Loc.T(installing ? "Installing" : game.Installed ? "Installed" : "Not installed");
        StorageValue.Text = game.Installed ? DisplayFormat.Size(game.SizeOnDisk) : "-";

        bool idle = !installing;
        bool stopped = epicStopped && !game.Installed && idle;
        if (stopped)
        {
            StatusDot.Fill = new SolidColorBrush(amber);
            StatusValue.Text = Loc.T("Partly downloaded");
        }
        PlayButton.Visibility = game.Installed && idle ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = (game.Installed && idle) || stopped ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Content = Loc.T(stopped ? "Delete the downloaded files" : game.Launcher == Launcher.Manual ? "Remove from shelf" : "Uninstall");
        InstallButton.Visibility = !game.Installed && idle ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.Content = Loc.T(stopped ? "Resume the download" : "Install");
        ProgressPanel.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Backdrop and store text, which may need a download: filled in when they arrive.</summary>
    async Task ShowOnlineInfoAsync()
    {
        var heroTask = CoverService.EnsureHeroAsync(game);
        var detailsTask = GameDetailsService.GetAsync(game.AppId);

        var heroPath = await heroTask;
        if (heroPath is not null)
        {
            Backdrop.Source = ImageLoader.Load(heroPath, 1200);
            Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0.32, TimeSpan.FromMilliseconds(500)));
        }

        var details = await detailsTask;
        DescriptionText.Text = details?.Summary(MaxDescriptionLength) ?? "";
        box.UpdateBack(details, heroPath);

        // Developer, publisher (when different) and release date, on one line.
        var publisher = details?.Publisher == details?.Developer ? null : details?.Publisher;
        MetaText.Text = string.Join("  ·  ",
            new[] { details?.Developer, publisher, details?.ReleaseDate }.Where(part => !string.IsNullOrWhiteSpace(part)));

        foreach (var genre in details?.Genres.Take(5) ?? Enumerable.Empty<string>())
            GenreChips.Children.Add(CreateChip(genre));
    }

    /// <summary>
    /// A chip with the logo and name of a launcher the game is owned on. The current one has a gold edge; another one
    /// opens that launcher's page of the game.
    /// </summary>
    Border CreatePlatformChip(Launcher launcher)
    {
        var chip = CreateChip(game.LabelOf(launcher));
        var label = (TextBlock)chip.Child;
        chip.Child = null; // the label moves into the panel below: it can only have one parent
        chip.Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { PlatformLogos.Create(launcher, 13, console: game.Console), label },
        };
        label.Margin = new Thickness(6, 0, 0, 0);

        if (game.OwnedOn.Count > 1 && launcher == game.Launcher)
        {
            chip.BorderBrush = new SolidColorBrush(Color.FromRgb(0xd9, 0xb7, 0x7a));
            chip.BorderThickness = new Thickness(1);
        }
        else if (game.Siblings.FirstOrDefault(sibling => sibling.Launcher == launcher) is { } other)
        {
            chip.Cursor = Cursors.Hand;
            chip.ToolTip = Loc.T("Open the {0} version", launcher.DisplayName());
            chip.MouseLeftButtonUp += (_, _) => SwitchRequested?.Invoke(other);
        }
        return chip;
    }

    static Border CreateChip(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255)),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(11, 3, 11, 4),
        Margin = new Thickness(0, 0, 7, 7),
        Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xdd, 0xd6, 0xc8)), FontSize = 12 },
    };

    // ---- Keeping in sync with Steam ----

    /// <summary>The game may already be downloading, started from Steam or from a previous visit to this window.</summary>
    async Task DetectRunningInstallAsync()
    {
        if (game.Installed) return;
        var progress = game.Launcher == Launcher.Steam ? await SteamInstaller.GetProgressAsync(game.AppId) : await EpicProgressAsync();
        if (epicStopped) ApplyInstallState();
        if (progress is { Found: true, Completed: false })
        {
            watchProgress = true;
            ShowProgress(progress);
        }
    }

    /// <summary>Every tick: reflect installs and uninstalls (from the manifest on disk) and the download progress.</summary>
    async Task SyncAsync()
    {
        if (syncing) return;
        syncing = true;
        try
        {
            SyncInstalledState();
            if (watchProgress)
                ShowProgress(game.Launcher == Launcher.Steam ? await SteamInstaller.GetProgressAsync(game.AppId) : await EpicProgressAsync());
        }
        finally
        {
            syncing = false;
        }
    }

    void SyncInstalledState()
    {
        if (game.Launcher != Launcher.Steam) return; // an Epic install is followed through the launcher (see EpicProgressAsync)

        var onDisk = SteamLibrary.ReadInstalledGame(game.AppId);
        bool installedOnDisk = onDisk is not null;
        if (installedOnDisk == game.Installed && (onDisk?.SizeOnDisk ?? 0) == game.SizeOnDisk) return; // nothing changed

        game.Installed = installedOnDisk;
        game.SizeOnDisk = onDisk?.SizeOnDisk ?? 0;
        game.InstallDir = onDisk?.InstallDir;
        if (game.Installed) installing = false;
        ApplyInstallState();
    }

    void ShowProgress(InstallProgress? progress)
    {
        bool starting = installStartedAt is { } started && DateTime.UtcNow - started < StartGracePeriod;
        bool downloading = !game.Installed && progress is { Found: true, Completed: false };
        installing = downloading || (starting && !game.Installed);
        if (!installing) watchProgress = false; // finished, cancelled, or never started: stop polling

        if (installing)
        {
            paused = progress is { Paused: true };
            PauseButton.Content = Loc.T(paused ? "Resume" : "Pause");
            InstallBar.IsIndeterminate = progress is not { Found: true };
            InstallBar.Value = progress?.Percent ?? 0;
            ProgressText.Text = DescribeState(progress);
            ProgressDetail.Text = DescribeDetail(progress);
            SpeedChart.Add(progress is { Paused: false } ? progress.BytesPerSecond : 0);
        }
        else
        {
            SpeedChart.Clear();
        }
        ApplyInstallState();
    }

    static string DescribeState(InstallProgress? progress)
    {
        if (progress is not { Found: true }) return Loc.T("Starting...");
        if (progress.Paused) return Loc.T("Paused  ·  {0}%", $"{progress.Percent:0}");
        if (!progress.Active) return Loc.T("Queued");
        // Steam's own wording for the stage (Downloading, Staging...) is shown as it comes.
        var state = progress.State is { Length: > 0 } and not "None" ? Loc.T(progress.State) : Loc.T("Downloading");
        return $"{state}  ·  {progress.Percent:0}%";
    }

    /// <summary>Speed, time left and bytes done. The total is only known when this window started the install.</summary>
    string DescribeDetail(InstallProgress? progress)
    {
        if (progress is not { Found: true, Active: true, Paused: false }) return "";
        var parts = new List<string>();
        if (progress.BytesPerSecond > 0) parts.Add(DisplayFormat.Speed(progress.BytesPerSecond));
        if (progress.SecondsRemaining > 0) parts.Add(DisplayFormat.TimeLeft(progress.SecondsRemaining));
        if (installTotalBytes > 0)
        {
            var done = (long)(installTotalBytes * progress.Percent / 100);
            parts.Add(Loc.T("{0} of {1}", DisplayFormat.Size(done), DisplayFormat.Size(installTotalBytes)));
        }
        return string.Join("  ·  ", parts);
    }

    // ---- Actions ----

    void OnPlayClick(object sender, RoutedEventArgs e)
    {
        if (game.Launcher == Launcher.Steam) SteamControl.Play(game.AppId);
        else if (game.Launcher == Launcher.Gog) GogLibrary.Play(game);
        else if (game.Launcher == Launcher.Manual) ManualGames.Play(game);
        else if (game.LaunchUri is { } uri) OpenUri(uri);
    }

    static void OpenUri(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (game.Launcher != Launcher.Steam)
        {
            await InstallEpicAsync();
            return;
        }

        InstallButton.IsEnabled = false;
        InstallButton.Content = Loc.T("Preparing...");
        try
        {
            // The plan asked when the window opened is used, unless it is old (free space changes).
            if (plan is null || DateTime.UtcNow - planAskedAt > TimeSpan.FromMinutes(2)) PreparePlan();
            var installPlan = await plan!;
            if (installPlan is null)
            {
                SteamControl.Install(game.AppId); // Steam's debug mode is off: let Steam show its own install window
                return;
            }

            InstallButton.Content = Loc.T("Install");
            var dialog = new InstallDialog(game, installPlan);
            DialogHost.ShowModal(Host, dialog);
            if (dialog.Request is not { } request) return;

            // Show the progress panel right away: Steam takes a few seconds to take the request.
            installTotalBytes = installPlan.RequiredBytes;
            installStartedAt = DateTime.UtcNow;
            watchProgress = true;
            ShowProgress(null);

            bool started = await SteamInstaller.StartAsync(game.AppId, request);
            Host.Activate(); // Steam's windows are kept hidden, but make sure this one stays in front
            if (!started)
            {
                installStartedAt = null;
                ShowProgress(null);
                MessageDialog.Info(Host,Loc.T("Could not start the installation"),
                    Loc.T("Steam did not accept the installation. Try again, or install the game from Steam."));
            }
        }
        finally
        {
            plan = null; // the next install asks again
            InstallButton.Content = Loc.T("Install");
            InstallButton.IsEnabled = true;
        }
    }

    async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (game.Launcher == Launcher.Epic)
        {
            await UninstallEpicAsync();
            return;
        }
        if (game.Launcher == Launcher.Manual)
        {
            if (MessageDialog.Confirm(Host, Loc.T("Remove {0} from the shelf?", game.Name),
                    Loc.T("The program and its files are not touched."),
                    confirmText: Loc.T("Remove"), cancelText: Loc.T("Keep it"), destructive: true))
            {
                ManualGames.Remove(game);
                Close();
            }
            return;
        }
        if (game.Launcher == Launcher.Gog)
        {
            // The game's own uninstaller asks for confirmation; without one, Galaxy's page of the game is the way left.
            if (!GogLibrary.Uninstall(game) && game.InstallUri is { } galaxyPage) OpenUri(galaxyPage);
            return;
        }
        if (!MessageDialog.Confirm(Host, Loc.T("Uninstall {0}?", game.Name),
                Loc.T("The game files will be deleted from your disk. Your Steam Cloud saves are kept."),
                confirmText: Loc.T("Uninstall"), cancelText: Loc.T("Keep it"), destructive: true))
            return;

        UninstallButton.IsEnabled = false;
        try
        {
            // Without Steam's debug mode, hand over to Steam's own uninstall window.
            if (!await SteamInstaller.UninstallAsync(game.AppId)) SteamControl.Uninstall(game.AppId);
        }
        finally
        {
            UninstallButton.IsEnabled = true; // the sync tick flips the window to "Not installed" once the files are gone
        }
    }

    async void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (game.Launcher == Launcher.Epic && EpicLibrary.IdentityOf(game.AppId) is { } epic)
        {
            epicPaused = !paused;
            if (!await (paused ? EpicBridge.ResumeAsync(epic) : EpicBridge.PauseAsync(epic))) epicPaused = paused;
            return;
        }
        if (paused) await SteamInstaller.ResumeAsync(game.AppId);
        else await SteamInstaller.PauseAsync(game.AppId);
    }

    async void OnCancelInstallClick(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Confirm(Host, Loc.T("Stop the download?"),
                Loc.T("{0} will stop downloading, and what was already downloaded will be deleted. You can start the installation again at any time.", game.Name),
                confirmText: Loc.T("Stop download"), cancelText: Loc.T("Keep downloading"), destructive: true))
            return;

        bool stopped = game.Launcher == Launcher.Epic && EpicLibrary.IdentityOf(game.AppId) is { } epic
            ? await EpicBridge.CancelAsync(epic)
            : await SteamInstaller.CancelAsync(game.AppId);
        if (!stopped)
        {
            // Do not pretend it stopped: the download is still running in the launcher.
            MessageDialog.Info(Host, Loc.T("Could not stop the download"),
                Loc.T("The launcher did not accept the request. Stop the download from its own downloads page."));
            return;
        }
        epicPaused = false;
        installStartedAt = null;
        watchProgress = false;
        installing = false;
        epicStopped = false;
        ApplyInstallState();
    }

    // ---- Epic: installing and removing through the launcher ----

    /// <summary>
    /// The launcher must run with its debug port for GameShelf to control it. Offers to restart it that way; false if
    /// the user prefers to use the launcher, or it could not be started.
    /// </summary>
    async Task<bool> EnsureEpicBridgeAsync()
    {
        if (await EpicBridge.IsReachableAsync()) return true;
        if (!MessageDialog.Confirm(Host, Loc.T("Control Epic from GameShelf"),
                Loc.T("To install and remove Epic games from GameShelf, without opening the launcher, GameShelf restarts the Epic Games Launcher with a local debug port. While it runs this way, other programs on this PC could also use that port. GameShelf closes the launcher again once the download is done."),
                Loc.T("Restart Epic"), Loc.T("Use the launcher")))
            return false;
        InstallButton.Content = Loc.T("Starting Epic...");
        return await EpicBridge.RestartWithDebugPortAsync();
    }

    async Task InstallEpicAsync()
    {
        if (EpicLibrary.IdentityOf(game.AppId) is not { } epic)
        {
            if (game.InstallUri is { } uri) OpenUri(uri);
            return;
        }

        InstallButton.IsEnabled = false;
        InstallButton.Content = Loc.T("Preparing...");
        try
        {
            if (!await EnsureEpicBridgeAsync())
            {
                if (game.InstallUri is { } uri) OpenUri(uri); // the launcher shows its own install window
                return;
            }

            if (epicStopped)
            {
                // Finish what was downloaded: no question to ask, the folder is already chosen.
                installStartedAt = DateTime.UtcNow;
                epicPaused = false;
                epicStopped = false;
                epicSamples.Clear();
                watchProgress = true;
                ShowProgress(null);
                if (!await EpicBridge.ResumeAsync(epic) && !await EpicBridge.InstallAsync(epic))
                {
                    installStartedAt = null;
                    epicStopped = true;
                    ShowProgress(null);
                    MessageDialog.Info(Host, Loc.T("Could not start the installation"),
                        Loc.T("The Epic Games Launcher did not accept the installation. Try again, or install the game from the launcher."));
                }
                return;
            }

            var location = await EpicBridge.GetInstallLocationAsync(epic);
            if (!MessageDialog.Confirm(Host, Loc.T("Install {0}", game.Name),
                    location is null
                        ? Loc.T("The Epic Games Launcher downloads the game.")
                        : Loc.T("The Epic Games Launcher downloads the game into {0}. You can change the install folder in the launcher's settings.", location),
                    Loc.T("Install")))
                return;

            installStartedAt = DateTime.UtcNow;
            epicPaused = false;
            epicSamples.Clear();
            watchProgress = true;
            ShowProgress(null);
            if (!await EpicBridge.InstallAsync(epic))
            {
                installStartedAt = null;
                ShowProgress(null);
                MessageDialog.Info(Host, Loc.T("Could not start the installation"),
                    Loc.T("The Epic Games Launcher did not accept the installation. Try again, or install the game from the launcher."));
            }
        }
        finally
        {
            ApplyInstallState(); // the button text for the state the game is in now
            InstallButton.IsEnabled = true;
        }
    }

    async Task UninstallEpicAsync()
    {
        if (EpicLibrary.IdentityOf(game.AppId) is not { } epic)
        {
            OpenUri(EpicLibrary.LibraryUri);
            return;
        }
        bool leftovers = epicStopped && !game.Installed;
        if (!MessageDialog.Confirm(Host, leftovers ? Loc.T("Delete the downloaded files?") : Loc.T("Uninstall {0}?", game.Name),
                leftovers
                    ? Loc.T("The part of {0} that was downloaded will be deleted from your disk.", game.Name)
                    : Loc.T("The game files will be deleted from your disk. Your Epic cloud saves are kept."),
                confirmText: leftovers ? Loc.T("Delete files") : Loc.T("Uninstall"), cancelText: Loc.T("Keep it"), destructive: true))
            return;

        UninstallButton.IsEnabled = false;
        try
        {
            if (!await Task.Run(() => EpicBridge.UninstallAsync(epic)))
            {
                MessageDialog.Info(Host, Loc.T("Could not uninstall the game"),
                    Loc.T("Some files could not be deleted. Close the game and try again."));
                return;
            }
            epicStopped = false;
            game.Installed = false;
            game.SizeOnDisk = 0;
            game.InstallDir = null;
            ApplyInstallState();
        }
        finally
        {
            UninstallButton.IsEnabled = true;
        }
    }

    /// <summary>The Epic game's install state from the launcher, as the progress model of the page. Null if not reachable.</summary>
    async Task<InstallProgress?> EpicProgressAsync()
    {
        if (EpicLibrary.IdentityOf(game.AppId) is not { } epic || await EpicBridge.GetStateAsync(epic) is not { } state) return null;
        epicStopped = EpicBridge.IsStopped(state) && !epicPaused && !state.StatusText.Contains("paus", StringComparison.OrdinalIgnoreCase);
        if (state.Installed && !state.Installing)
        {
            game.Installed = true;
            game.InstallDir = state.InstallLocation;
            game.SizeOnDisk = state.SizeOnDisk;
        }
        var progress = EpicBridge.ToProgress(state, epicPaused || state.StatusText.Contains("paus", StringComparison.OrdinalIgnoreCase));
        return state.Installing && state.InstallLocation.Length > 0 ? await WithMeasuredSpeedAsync(progress, state.InstallLocation) : progress;
    }

    /// <summary>
    /// The launcher gives a percentage only. The speed is measured from how fast the install folder grows (over about
    /// eight seconds), and the total size and time left are estimated from it and the percentage.
    /// </summary>
    async Task<InstallProgress> WithMeasuredSpeedAsync(InstallProgress progress, string folder)
    {
        long written = await Task.Run(() => FolderSize(folder));
        if (written <= 0) return progress;

        var now = DateTime.UtcNow;
        epicSamples.Enqueue((now, written));
        while (epicSamples.Count > 1 && (now - epicSamples.Peek().At).TotalSeconds > 8) epicSamples.Dequeue();

        long speed = 0;
        var first = epicSamples.Peek();
        double seconds = (now - first.At).TotalSeconds;
        if (seconds >= 1 && written >= first.Bytes) speed = (long)((written - first.Bytes) / seconds);
        if (progress.Paused) speed = 0;

        int secondsLeft = 0;
        if (progress.Percent >= 1)
        {
            installTotalBytes = (long)(written * 100 / progress.Percent);
            if (speed > 0) secondsLeft = (int)Math.Min(int.MaxValue, Math.Max(0, (installTotalBytes - written) / speed));
        }
        return progress with { BytesPerSecond = speed, SecondsRemaining = secondsLeft };
    }

    static long FolderSize(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0; // the launcher is creating or moving files: the next tick measures again
        }
    }

    /// <summary>Edits a game added by hand; the shelf reads the list again and this page, which shows the old values, closes.</summary>
    void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (AddGameDialog.Show(Host, game)) Close();
    }

    void OnHideClick(object sender, RoutedEventArgs e)
    {
        HiddenGames.Toggle(game.KeyId);
        Close();
    }
}
