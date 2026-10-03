using System.Diagnostics;
using System.Globalization;
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
/// The shelf: loads the library, lays the game spines out on planks, and hosts the search, the filters
/// and the skin picker.
/// </summary>
internal sealed partial class MainWindow : Window
{
    const string SkinSetting = "skin.txt";

    const int ParallelCoverLoads = 8;
    static readonly TimeSpan MaxWaitBeforeOpening = TimeSpan.FromMilliseconds(1200);
    const double ShelfRowHeight = 255; // tallest hovered spine (240 * 1.05) fits in it
    const double SpineSlotWidth = SpineView.SpineWidth + 2; // spine plus its 1px side margins
    const int MaxRecentlyPlayed = 12;
    const double FullscreenScale = 1.35; // bigger spines for a TV

    static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xd9, 0xb7, 0x7a));

    /// <summary>Where the game list came from; <see cref="Live"/> is the only exact one.</summary>
    enum LibrarySource { Live, LastKnown, LocalScan }

    readonly List<SpineView> spines = new();

    /// <summary>The spines the shelf shows: a title owned on several launchers has one, the other copies are reached from its page.</summary>
    IEnumerable<SpineView> ShelfSpines => spines.Where(s => !s.Game.IsAlternate);
    readonly List<List<SpineView>> layoutRows = new(); // the spines as laid out now, for arrow-key navigation
    readonly DispatcherTimer relayoutTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly ShelfFilter filter = new();
    Style ChipStyle => (Style)FindResource("ChipToggle");
    DateTime filterPopupClosedAt;
    DateTime languagePopupClosedAt;
    string skinName = Skins.Names[0];
    Skin skin = Skins.Get(Skins.Names[0]);
    LibrarySource source;
    DetailView? detail;          // the game page, while one is open
    UpdateInfo? availableUpdate;
    SpineView? focused;         // chosen with the keyboard or the controller
    Point lastMousePosition;
    bool fullscreen;
    WindowState windowStateBeforeFullscreen;

    public MainWindow()
    {
        InitializeComponent();
        WindowPlacement.Restore(this);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width); // a small screen
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        ManualGames.Changed += () => Dispatcher.InvokeAsync(RefreshManualGamesAsync); // a game was added, changed or removed by hand
        Closing += (_, _) => WindowPlacement.Save(this, fullscreen ? windowStateBeforeFullscreen == WindowState.Maximized : WindowState == WindowState.Maximized);
        DarkTitleBar.Apply(this);
        Loc.Apply(this);
        LanguageButton.Content = Flags.Create(Loc.Current.Code);
        BuildLanguageChips();

        // Resizing fires many events: lay the shelf out once the window settles.
        relayoutTimer.Tick += (_, _) => { relayoutTimer.Stop(); RebuildShelf(); };

        // Typing: filter once the user pauses for a moment, not on every key.
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); RebuildShelf(); };
        Search.TextChanged += () =>
        {
            filter.Query = Search.Text;
            searchTimer.Stop();
            searchTimer.Start();
        };
        Filters.Changed += OnFiltersChanged;
        GameCollections.Changed += () => _ = Dispatcher.BeginInvoke(new Action(OnCollectionsChanged));
        BuildCollectionBar();

        var savedSkin = AppData.ReadText(SkinSetting)?.Trim();
        if (savedSkin is not null && Skins.Names.Contains(savedSkin)) skinName = savedSkin;
        BuildSkinChips();
        ApplySkin();

        Gamepad.Pressed += OnGamepadPressed;
        Gamepad.Start();

        Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--bigpicture")) ToggleFullscreen();
            _ = CheckForUpdateAsync(); // in the background: the shelf does not wait for GitHub
            await LoadAsync(offerExactMode: true);
        };
    }

    // ---- Loading ----

    /// <summary>
    /// Reads the library, builds the spines, then fetches every cover and logo. The loading screen stays
    /// up the whole time so the shelf never appears half empty.
    /// </summary>
    async Task LoadAsync(bool offerExactMode, bool ignoreNeverAskAgain = false)
    {
        Splash.Opacity = 1;
        Splash.Visibility = Visibility.Visible;
        SplashBar.IsIndeterminate = true;
        spines.Clear();
        ShelfRows.Children.Clear();
        try
        {
            var library = await ResolveLibraryAsync(offerExactMode, ignoreNeverAskAgain);
            ExactButton.Visibility = source == LibrarySource.LocalScan ? Visibility.Visible : Visibility.Collapsed;

            var epic = ReadEpicLibrary();

            SetSplash(Loc.T("Preparing the shelf..."));
            var games = await Task.Run(() =>
            {
                var list = SteamLibrary.Scan(library);
                SteamMetadata.Fill(list); // genres, features, play time... for the search and filters (Steam only)
                list.AddRange(EpicLibrary.Scan(epic));
                list.AddRange(GogLibrary.Scan());
                list.AddRange(ManualGames.Scan());
                return list;
            });
            Platforms.Match(games); // the same title on several launchers: every logo on each cover
            foreach (var game in games)
            {
                var spine = new SpineView(game, ColorUtil.PlaceholderFor(game.AppId));
                HookSpine(spine);
                spines.Add(spine);
            }
            BindFilterOptions();
            RebuildShelf();
            RefreshGameUpdatesButton();

            await LoadArtworkAsync();
            RebuildShelf(); // the "Recently played" row holds copies of the spines: redraw them with their artwork
            if (!epicRefreshed && EpicClient.IsSignedIn)
            {
                epicRefreshed = true;
                _ = RefreshEpicAsync();
            }
        }
        catch (Exception e)
        {
            StatusText.Text = e.Message;
        }
        finally
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
            fade.Completed += (_, _) => Splash.Visibility = Visibility.Collapsed;
            Splash.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>
    /// Best available game list: the live Steam client, else the last one saved, else null (local scan).
    /// Offers to turn on Steam's debug mode when the live list is not reachable.
    /// </summary>
    async Task<Dictionary<uint, string>?> ResolveLibraryAsync(bool offerExactMode, bool ignoreNeverAskAgain)
    {
        SetSplash(Loc.T("Reading your Steam library..."));
        var library = await SteamClient.GetLibraryAsync();
        if (library is null && offerExactMode && (ignoreNeverAskAgain || SteamControl.ShouldPrompt))
            library = await AskToEnableExactModeAsync();

        if (library is not null)
        {
            source = LibrarySource.Live;
            SteamClient.SaveCache(library);
            return library;
        }

        library = SteamClient.LoadCache();
        source = library is null ? LibrarySource.LocalScan : LibrarySource.LastKnown;
        return library;
    }

    bool epicRefreshed;

    /// <summary>The "Updates" button shows how many Steam games have an update waiting (known from files on this PC).</summary>
    void RefreshGameUpdatesButton()
    {
        int count = PendingUpdates.OnDisk(spines.Select(s => s.Game).Where(g => g.Installed).ToList()).Count;
        GameUpdatesButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GameUpdatesButton.Content = Loc.T("Updates ({0})", count);
    }

    /// <summary>
    /// The games with an update waiting: Steam's from the manifests on this PC, Epic's from the launcher when it runs in
    /// the mode GameShelf controls it in (otherwise a line says they could not be checked).
    /// </summary>
    async void OnGameUpdatesClick(object sender, RoutedEventArgs e)
    {
        var installed = spines.Select(s => s.Game).Where(g => g.Installed).ToList();
        var updates = PendingUpdates.OnDisk(installed);
        string? note = null;
        if (installed.Any(g => g.Launcher == Launcher.Epic))
        {
            if (await EpicBridge.IsReachableAsync()) updates.AddRange(await PendingUpdates.EpicAsync(installed));
            else note = Loc.T("The updates of your Epic games can only be checked while GameShelf controls the Epic Games Launcher (install a game from GameShelf to start it that way).");
        }
        UpdatesPanel.Show(this, updates.OrderBy(u => u.Game.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), note);
        RefreshGameUpdatesButton();
    }

    /// <summary>The owned Epic games as last saved (null before the first sign-in). They are refreshed in the background.</summary>
    List<OwnedEpicGame>? ReadEpicLibrary()
    {
        UpdateEpicButton();
        return EpicClient.LoadCache();
    }

    /// <summary>The button signs in to Epic, or signs out once signed in.</summary>
    void UpdateEpicButton()
    {
        EpicButton.Visibility = EpicLibrary.IsLauncherInstalled || EpicClient.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        EpicButton.Content = Loc.T(EpicClient.IsSignedIn ? "Sign out of Epic" : "Epic library");
    }

    void OnAddGameClick(object sender, RoutedEventArgs e) => AddGameDialog.Show(this);

    /// <summary>
    /// Once per run, reads the Epic library again with the stored token, so new purchases show up on their own. The
    /// shelf is rebuilt only if the list changed. Offline or Epic down: the saved list stays.
    /// </summary>
    async Task RefreshEpicAsync()
    {
        var before = EpicClient.LoadCache()?.Select(game => game.AppName).ToHashSet() ?? new HashSet<string>();
        List<OwnedEpicGame>? fresh;
        try
        {
            fresh = await EpicClient.RefreshAsync();
        }
        catch (EpicException)
        {
            return;
        }
        UpdateEpicButton(); // the token may have been refused and deleted: the button asks to sign in again
        if (fresh is not null && !fresh.Select(game => game.AppName).ToHashSet().SetEquals(before))
            await LoadAsync(offerExactMode: false);
    }

    /// <summary>Signs out of Epic, or signs in: Epic's page in a window, the code it hands over, the library, then the shelf reloads.</summary>
    async void OnEpicClick(object sender, RoutedEventArgs e)
    {
        if (EpicClient.IsSignedIn)
        {
            if (!MessageDialog.Confirm(this, Loc.T("Sign out of Epic?"),
                    Loc.T("GameShelf forgets your Epic sign-in and the list of your Epic games. You can sign in again at any time."),
                    Loc.T("Sign out"), Loc.T("Not now")))
                return;
            EpicClient.SignOut();
            await LoadAsync(offerExactMode: false);
            return;
        }

        if (!MessageDialog.Confirm(this, Loc.T("Import your Epic Games library"),
                Loc.T("GameShelf shows Epic's own sign-in page. Sign in and GameShelf reads the games you own, installed or not, and refreshes the list each time it starts. Your password never goes through GameShelf. It keeps a sign-in token, encrypted for your Windows account, until you sign out."),
                Loc.T("Sign in to Epic"), Loc.T("Not now")))
            return;

        var code = EpicSignInPanel.Ask(this, out bool browserAvailable);
        if (!browserAvailable)
        {
            // No WebView2 on this PC: sign in in the default browser and paste the code Epic shows.
            Process.Start(new ProcessStartInfo(EpicClient.SignInUrl) { UseShellExecute = true });
            code = InputDialog.Ask(this, Loc.T("Paste the code Epic shows"), Loc.T("Import"),
                text => text.Length == 0 ? Loc.T("Paste the code first.") : null, maxLength: 1000);
        }
        if (code is null) return;

        Splash.Opacity = 1;
        Splash.Visibility = Visibility.Visible;
        SplashBar.IsIndeterminate = true;
        SetSplash(Loc.T("Reading your Epic library..."));
        try
        {
            await EpicClient.ImportAsync(code);
        }
        catch (EpicException error)
        {
            MessageDialog.Info(this, Loc.T("Could not read the Epic library"), error.Message);
        }
        epicRefreshed = true; // the list was just read
        await LoadAsync(offerExactMode: false);
    }

    async Task<Dictionary<uint, string>?> AskToEnableExactModeAsync()
    {
        var dialog = new ExactModeDialog();
        DialogHost.ShowModal(this, dialog);
        switch (dialog.Choice)
        {
            case ExactModeChoice.Never:
                SteamControl.NeverAsk();
                return null;
            case ExactModeChoice.Enable:
                return await EnableExactModeAsync();
            default:
                return null;
        }
    }

    /// <summary>Turns on Steam's debug mode, restarts Steam and waits for its library. Null if anything fails.</summary>
    async Task<Dictionary<uint, string>?> EnableExactModeAsync()
    {
        if (!SteamControl.TryEnableDebugMode(out var error))
        {
            MessageDialog.Info(this, Loc.T("Could not enable the exact library"),
                Loc.T("Steam's debug mode could not be turned on: {0}", error ?? ""));
            return null;
        }
        SetSplash(Loc.T(SteamControl.IsRunning ? "Restarting Steam..." : "Starting Steam..."));
        await SteamControl.RestartAsync();
        SetSplash(Loc.T("Waiting for Steam (sign in if Steam asks you to)..."));
        return await SteamClient.WaitForLibraryAsync(TimeSpan.FromMinutes(3));
    }

    /// <summary>Fetches each game's cover and logo, then tints and redraws its spine as the artwork arrives.</summary>
    async Task LoadArtworkAsync()
    {
        SplashBar.IsIndeterminate = false;
        SplashBar.Maximum = Math.Max(1, spines.Count);
        SplashBar.Value = 0;

        var limiter = new SemaphoreSlim(ParallelCoverLoads);
        await Task.WhenAll(spines.Select(async spine =>
        {
            await limiter.WaitAsync();
            try
            {
                await LoadArtworkOfAsync(spine);
            }
            finally
            {
                limiter.Release();
                SplashBar.Value++;
                SetSplash(Loc.T("Loading covers... {0}/{1}", (int)SplashBar.Value, spines.Count));
            }
        }));
    }

    static async Task LoadArtworkOfAsync(SpineView spine)
    {
        try
        {
            var coverPath = await CoverService.EnsureCoverAsync(spine.Game);
            spine.Game.Cover = coverPath;
            spine.Game.Logo = await CoverService.EnsureLogoAsync(spine.Game);
            if (coverPath is not null) spine.SetPalette(await Task.Run(() => ColorUtil.Palette(coverPath)));
            else spine.Refresh();
        }
        catch (Exception)
        {
            // One broken or unreadable image must not stop the others: that spine keeps its placeholder look.
        }
    }

    /// <summary>
    /// A game was added, edited or removed by hand: only the games added by hand are read again and redrawn, with no
    /// splash screen and nothing else reloaded. After an edit, the game's page stays open with the new values.
    /// </summary>
    async Task RefreshManualGamesAsync()
    {
        var edited = ManualGames.TakeEdited();
        spines.RemoveAll(s => s.Game.Launcher == Launcher.Manual);
        var added = new List<SpineView>();
        foreach (var game in ManualGames.Scan())
        {
            var spine = new SpineView(game, ColorUtil.PlaceholderFor(game.AppId));
            HookSpine(spine);
            spines.Add(spine);
            added.Add(spine);
        }
        BindFilterOptions();
        RebuildShelf();

        await Task.WhenAll(added.Select(LoadArtworkOfAsync));
        RebuildShelf();
        // The page of the edited game, if it is open (it normally is), shows the new values where it stands.
        if (edited is { } id && detail?.AppId == id && added.FirstOrDefault(s => s.Game.AppId == id) is { } updated)
            detail.Reload(updated.Game, updated.Color);
    }

    void SetSplash(string text) => SplashText.Text = text;

    // ---- Shelf layout ----

    /// <summary>
    /// Lays the spines out in rows that fit the window width, one plank under each row. Every row gets the
    /// same number of spines (no ragged last row) and is centred.
    /// </summary>
    void RebuildShelf()
    {
        if (spines.Count == 0) return;

        foreach (var spine in spines) (spine.Parent as Panel)?.Children.Remove(spine);
        ShelfRows.Children.Clear();
        layoutRows.Clear();

        bool showHidden = ShowHiddenToggle.IsChecked == true;
        var candidates = ShelfSpines.Where(s => showHidden || !HiddenGames.Contains(s.Game.KeyId)).ToList();
        var shown = filter.Order(candidates.Where(s => filter.Matches(s.Game)), s => s.Game);
        UpdateHeader(shown.Count, candidates.Count);

        if (shown.Count == 0)
        {
            ShelfRows.Children.Add(CreateEmptyState());
            RestoreFocus();
            return;
        }

        double availableWidth = Math.Max(300, (ShelfScroll.ActualWidth - 110) / ShelfScale);
        int maxPerRow = Math.Max(1, (int)(availableWidth / SpineSlotWidth));

        if (!filter.IsActive) AddRecentlyPlayed(maxPerRow);

        var groups = ShelfGrouping.Group(filter.Group, shown, s => s.Game, keepEmptyCollections: !filter.IsActive);
        foreach (var group in groups)
        {
            if (group.Title.Length > 0) ShelfRows.Children.Add(CreateGroupBanner(group));
            if (group.Items.Count > 0)
            {
                AddRows(group.Items, maxPerRow);
            }
            else
            {
                ShelfRows.Children.Add(new Border { Height = 44 }); // an empty collection: just a plank to drop on
                ShelfRows.Children.Add(CreatePlank());
            }
        }
        RestoreFocus();
    }

    /// <summary>
    /// Adds the spines in rows of the same size, centred, with a plank under each row (no ragged last row).
    /// </summary>
    void AddRows(List<SpineView> items, int maxPerRow)
    {
        int rowCount = (int)Math.Ceiling(items.Count / (double)maxPerRow);
        int perRow = (int)Math.Ceiling(items.Count / (double)rowCount);

        for (int start = 0; start < items.Count; start += perRow)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var rowSpines = items.Skip(start).Take(perRow).ToList();
            foreach (var spine in rowSpines)
            {
                spine.Opacity = HiddenGames.Contains(spine.Game.KeyId) ? 0.4 : 1;
                row.Children.Add(spine);
            }
            layoutRows.Add(rowSpines);
            ShelfRows.Children.Add(new Border { Height = ShelfRowHeight, Child = row });
            ShelfRows.Children.Add(CreatePlank());
        }
    }

    /// <summary>
    /// A shelf at the top with the games played last. It holds copies of the spines, as a spine can only be in
    /// one place. Left out while searching or filtering.
    /// </summary>
    void AddRecentlyPlayed(int maxPerRow)
    {
        var recent = ShelfSpines
            .Where(s => s.Game.LastPlayed is not null && !HiddenGames.Contains(s.Game.KeyId))
            .OrderByDescending(s => s.Game.LastPlayed)
            .Take(Math.Min(maxPerRow, MaxRecentlyPlayed))
            .ToList();
        if (recent.Count == 0) return;

        var last = recent[0].Game.LastPlayed!.Value.ToString("d MMM", CultureInfo.CurrentCulture);
        ShelfRows.Children.Add(CreateBanner(Loc.T("Recently played"), Loc.T("last session {0}", last)));
        AddRows(recent.Select(CloneSpine).ToList(), maxPerRow);
    }

    SpineView CloneSpine(SpineView source)
    {
        var clone = new SpineView(source.Game, source.Color);
        if (source.Palette is { } palette) clone.SetPalette(palette);
        HookSpine(clone);
        return clone;
    }

    void HookSpine(SpineView spine)
    {
        spine.Clicked += OpenDetails;
        spine.Dwelled += s => _ = PrefetchDetails(s);
    }

    /// <param name="shownCount">Games matching the search and filters.</param>
    /// <param name="candidateCount">Games before the search and filters (hidden ones left out unless shown).</param>
    void UpdateHeader(int shownCount, int candidateCount)
    {
        int hidden = ShelfSpines.Count(s => HiddenGames.Contains(s.Game.KeyId));
        int installed = ShelfSpines.Count(s => s.Game.Installed && !HiddenGames.Contains(s.Game.KeyId));

        var games = filter.IsActive ? Loc.T("{0} of {1} games", shownCount, candidateCount) : Loc.T("{0} games", candidateCount);
        StatusText.Text = Loc.T("{0}  ·  {1} installed", games, installed) + source switch
        {
            LibrarySource.LastKnown => "  ·  " + Loc.T("last known library, Steam is closed"),
            LibrarySource.LocalScan => "  ·  " + Loc.T("approximate library"),
            _ => "",
        };
        ShowHiddenToggle.Content = Loc.T("Show hidden ({0})", hidden);
        ShowHiddenToggle.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The button shows how many filters are on, and is gold while any is.
        bool filtering = filter.ActiveCount > 0;
        FiltersButton.Content = filtering ? Loc.T("Filters  ·  {0}", filter.ActiveCount) : Loc.T("Filters");
        FiltersButton.BorderBrush = filtering
            ? new SolidColorBrush(Color.FromRgb(0xd9, 0xb7, 0x7a))
            : new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255));
        Filters.ShowResultCount(shownCount, candidateCount);
    }

    /// <summary>Shown instead of the shelf when no game matches the search and filters.</summary>
    StackPanel CreateEmptyState()
    {
        var clear = new Button
        {
            Content = Loc.T("Clear search and filters"),
            Style = (Style)FindResource("SecondaryButton"),
            Height = 38,
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        clear.Click += (_, _) => ResetFilters();

        var panel = new StackPanel { Margin = new Thickness(0, 120, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("No game matches"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xe0, 0xd2)),
            FontSize = 22,
            FontFamily = new FontFamily("Georgia"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Try fewer words or remove a filter."),
            Foreground = new SolidColorBrush(Color.FromRgb(0xa8, 0x9f, 0x90)),
            FontSize = 14,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(clear);
        return panel;
    }

    // ---- Banners and collections ----

    /// <summary>Title of a group. In collection mode it is also a drop target for adding games to that collection.</summary>
    static Grid CreateGroupBanner(ShelfGroup<SpineView> group)
    {
        var subtitle = group.Items.Count == 0 && group.Collection is not null
            ? Loc.T("empty  ·  drag a game here")
            : Loc.Count(group.Items.Count, "{0} game", "{0} games");
        var banner = CreateBanner(Loc.T(group.Title), subtitle);
        if (group.Collection is { } collection)
            MakeDropTarget(banner, collection, on => banner.Background = on ? HighlightBrush : Brushes.Transparent);
        return banner;
    }

    /// <summary>A title over a shelf: name, a few words of detail, and a line running to the right edge.</summary>
    static Grid CreateBanner(string title, string detail)
    {
        var banner = new Grid { Margin = new Thickness(4, 10, 4, 12), Background = Brushes.Transparent };
        banner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        banner.ColumnDefinitions.Add(new ColumnDefinition());

        var text = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Georgia"),
            FontSize = 21,
            Foreground = new SolidColorBrush(Color.FromRgb(0xd9, 0xb7, 0x7a)),
        });
        text.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8a, 0x83, 0x78)),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(14, 0, 0, 3),
        });
        banner.Children.Add(text);

        var line = new Border
        {
            Height = 1,
            Margin = new Thickness(18, 4, 0, 0),
            Background = new LinearGradientBrush(Color.FromArgb(0x70, 0xd9, 0xb7, 0x7a), Colors.Transparent, 0),
        };
        Grid.SetColumn(line, 1);
        banner.Children.Add(line);
        return banner;
    }

    /// <summary>The bar under the header: "All games", one chip per collection, and "New".</summary>
    void BuildCollectionBar()
    {
        CollectionBar.Children.Clear();
        CollectionBar.Children.Add(new TextBlock
        {
            Text = Loc.T("COLLECTIONS"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x6f, 0x67, 0x5b)),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 6),
        });
        CollectionBar.Children.Add(CreateCollectionChip(Loc.T("All games"), null));
        foreach (var name in GameCollections.Names)
            CollectionBar.Children.Add(CreateCollectionChip($"{Loc.T(name)}  {GameCollections.Count(name)}", name));

        var add = new ToggleButton { Content = Loc.T("+  New"), Style = ChipStyle, ToolTip = Loc.T("Create a collection") };
        add.Click += (_, _) =>
        {
            add.IsChecked = false;
            CreateCollection();
        };
        CollectionBar.Children.Add(add);
    }

    ToggleButton CreateCollectionChip(string text, string? collection)
    {
        var chip = new ToggleButton
        {
            Content = text,
            Style = ChipStyle,
            IsChecked = filter.Collection == collection,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        chip.Click += (_, _) =>
        {
            filter.Collection = chip.IsChecked == true ? collection : null;
            BuildCollectionBar();
            RebuildShelf();
        };
        if (collection is not null)
        {
            chip.ToolTip = Loc.T("Show only these games  ·  drag a game here to add it  ·  right-click to delete");
            chip.MouseRightButtonUp += (_, _) => DeleteCollection(collection);
            MakeDropTarget(chip, collection, on => chip.RenderTransform = on ? new ScaleTransform(1.1, 1.1) : Transform.Identity);
        }
        return chip;
    }

    /// <summary>Lets a dragged spine be dropped on <paramref name="target"/> to add that game to a collection.</summary>
    static void MakeDropTarget(UIElement target, string collection, Action<bool> highlight)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(SpineView.DragFormat)) highlight(true);
        };
        target.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(SpineView.DragFormat) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        target.DragLeave += (_, _) => highlight(false);
        target.Drop += (_, e) =>
        {
            highlight(false);
            if (e.Data.GetData(SpineView.DragFormat) is uint appId) GameCollections.Add(collection, appId);
        };
    }

    void CreateCollection()
    {
        var name = InputDialog.Ask(this, Loc.T("New collection"), Loc.T("Create"), GameCollections.Validate);
        if (name is not null) GameCollections.Create(name);
    }

    void DeleteCollection(string name)
    {
        if (MessageDialog.Confirm(this, Loc.T("Delete \"{0}\"?", Loc.T(name)),
                Loc.T("Only the collection is removed. The games stay in your library."), Loc.T("Delete"), destructive: true))
            GameCollections.Delete(name);
    }

    /// <summary>Called after the collections or their content changed: redraw the bar and the shelf.</summary>
    void OnCollectionsChanged()
    {
        if (filter.Collection is { } selected && !GameCollections.Names.Contains(selected)) filter.Collection = null;
        BuildCollectionBar();
        RebuildShelf();
    }

    // ---- Search and filters ----

    /// <summary>Tells the filter panel which genres and features exist in this library, with how many games each.</summary>
    void BindFilterOptions()
    {
        var genres = ShelfSpines.SelectMany(s => s.Game.Genres)
            .GroupBy(genre => genre)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key)
            .Select(group => (group.Key, group.Count()))
            .ToList();
        var features = SteamMetadata.AllFeatures.Where(f => ShelfSpines.Any(s => s.Game.Features.Contains(f))).ToList();
        var launchers = ShelfSpines.SelectMany(s => s.Game.OwnedOn.Select(s.Game.LabelOf)).Distinct().Order().ToList();
        Filters.Bind(filter, genres, features, launchers);
    }

    void ResetFilters()
    {
        filter.Clear();
        Search.Text = "";
        Filters.Refresh();
        BuildCollectionBar();
        RebuildShelf();
    }

    void OnFiltersChanged()
    {
        if (Search.Text != filter.Query) Search.Text = filter.Query; // "Clear all" in the panel also empties the search
        BuildCollectionBar(); // "Clear all" also leaves the selected collection
        RebuildShelf();
    }

    void OnFiltersClick(object sender, RoutedEventArgs e)
    {
        // A click outside a popup closes it before the click lands: without this, the button would reopen it at once.
        if (DateTime.UtcNow - filterPopupClosedAt < TimeSpan.FromMilliseconds(250)) return;
        Filters.MaxHeight = Math.Max(240, ActualHeight - 160); // the panel scrolls in a short window
        FilterPopup.IsOpen = !FilterPopup.IsOpen;
    }

    void OnFilterPopupClosed(object? sender, EventArgs e) => filterPopupClosedAt = DateTime.UtcNow;

    /// <summary>
    /// Ctrl+F or "/" jump to the search, Escape clears it, arrows and Enter pick and open a game, F11 toggles
    /// fullscreen.
    /// </summary>
    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DialogHost.IsOpen) return; // a dialog handles its own keys
        if (detail is not null && e.Key != Key.F11) return; // the game page handles its own keys

        bool typing = Keyboard.FocusedElement is System.Windows.Controls.TextBox;
        bool plainKey = !typing && !FilterPopup.IsOpen && Keyboard.Modifiers == ModifierKeys.None;
        if (plainKey && TryNavigate(e.Key))
        {
            e.Handled = true;
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Search.FocusInput();
            e.Handled = true;
        }
        else if (e.Key == Key.OemQuestion && !typing && Keyboard.Modifiers == ModifierKeys.None)
        {
            Search.FocusInput(); // "/" on a US layout
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Search.Text.Length > 0)
        {
            Search.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && fullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    // ---- Updates ----

    /// <summary>Shows the "Update" button in the header when a newer release exists.</summary>
    async Task CheckForUpdateAsync()
    {
        availableUpdate = await UpdateChecker.CheckAsync();
        if (availableUpdate is null) return;
        UpdateButton.Content = Loc.T("Update to v{0}", availableUpdate.Version.ToString(3));
        UpdateButton.Visibility = Visibility.Visible;
    }

    async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is not { } update) return;
        var version = update.Version.ToString(3);
        if (!MessageDialog.Confirm(this, Loc.T("Update GameShelf to v{0}?", version),
                Loc.T("GameShelf downloads the new version, closes, and installs it. Start it again when the installer is done."),
                Loc.T("Update"), Loc.T("Later")))
            return;

        UpdateButton.IsEnabled = false;
        UpdateButton.Content = Loc.T("Downloading the update...");
        if (await UpdateChecker.DownloadAndStartAsync(update))
        {
            Application.Current.Shutdown();
            return;
        }
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = Loc.T("Update to v{0}", version);
        MessageDialog.Info(this, Loc.T("The update could not be downloaded"), Loc.T("Check your connection and try again."));
    }

    // ---- Events ----

    /// <summary>Starts fetching what the details window shows, so it is ready (or nearly) when the user clicks.</summary>
    static Task PrefetchDetails(SpineView spine) =>
        Task.WhenAll(GameDetailsService.GetAsync(spine.Game.AppId), CoverService.EnsureHeroAsync(spine.Game));

    async void OpenDetails(SpineView spine)
    {
        if (detail is not null) return; // a double click must not open two pages
        // Give the store text and artwork a moment to arrive so the page does not open half empty.
        var opening = new DetailView(spine.Game, spine.Color);
        detail = opening;
        await Task.WhenAny(PrefetchDetails(spine), Task.Delay(MaxWaitBeforeOpening));

        opening.Closed += () => CloseDetails(spine);
        opening.SwitchRequested += other =>
        {
            // The title is on another launcher too: close this page and open that one's.
            if (spines.FirstOrDefault(s => ReferenceEquals(s.Game, other)) is not { } target) return;
            opening.Close();
            OpenDetails(target);
        };
        DetailHost.Children.Add(opening);
        DetailHost.Visibility = Visibility.Visible;
        DetailHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
    }

    /// <summary>Takes the game page off the shelf and redraws what it may have changed.</summary>
    void CloseDetails(SpineView spine)
    {
        DetailHost.Children.Clear();
        DetailHost.Visibility = Visibility.Collapsed;
        detail = null;

        // The game may have been installed or uninstalled; the clicked spine can be a copy from the recent row.
        foreach (var s in spines.Where(s => s.Game.AppId == spine.Game.AppId)) s.Refresh();
        RebuildShelf(); // or hidden / restored
    }


    void OnShowHiddenClick(object sender, RoutedEventArgs e) => RebuildShelf();

    async void OnExactLibraryClick(object sender, RoutedEventArgs e) =>
        await LoadAsync(offerExactMode: true, ignoreNeverAskAgain: true);

    /// <summary>The header drops its extras as the window narrows (the counts stay in the title's tooltip), so nothing overlaps.</summary>
    void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        static Visibility ShownFrom(double width, double breakpoint) => width >= breakpoint ? Visibility.Visible : Visibility.Collapsed;
        double width = e.NewSize.Width;
        StatusText.Visibility = ShownFrom(width, 1350);
        ShelfLabel.Visibility = ShownFrom(width, 1150);
        TitleLabel.Visibility = ShownFrom(width, 1000);
        SkinChips.Visibility = ShownFrom(width, 900);
    }

    void OnShelfSizeChanged(object sender, SizeChangedEventArgs e)
    {
        relayoutTimer.Stop();
        relayoutTimer.Start();
    }
}
