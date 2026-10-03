using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameShelf.Launchers;
using GameShelf.Models;
using GameShelf.Services;
using Microsoft.Win32;

namespace GameShelf.Views;

/// <summary>
/// Adds a game that no launcher knows: its program (an emulator and its arguments too), its name, its console, and a
/// cover found by name, taken from a picture file, dropped or pasted, or drawn from the program's icon.
/// </summary>
internal sealed class AddGameDialog : StackPanel
{
    static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(0xd9, 0xb7, 0x7a));
    static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9a, 0x91, 0x83));
    static readonly Brush Edge = new SolidColorBrush(Color.FromRgb(0x3a, 0x35, 0x2f));

    readonly TextBox program = Field(), arguments = Field(), name = Field(), gameFile = Field();
    readonly StackPanel emulatorRow;
    readonly TextBlock emulatorHint = new() { Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly WrapPanel consoleChips = new() { Margin = new Thickness(0, 6, 0, 0) };
    EmulatorPreset? emulator;
    readonly WrapPanel covers = new() { Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock status = new() { Foreground = Muted, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock error = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xe0, 0x73, 0x5f)), FontSize = 12.5, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    readonly List<(Border Card, byte[] Picture)> candidates = new();
    readonly Game? editing;
    ConsoleChoice console = CoverSearch.Consoles[0];
    byte[]? chosen;

    /// <summary>True once an edit was saved.</summary>
    bool Saved { get; set; }

    AddGameDialog(Game? editing)
    {
        this.editing = editing;
        if (editing is not null && ManualGames.EntryOf(editing) is { } entry)
        {
            program.Text = entry.Exe;
            arguments.Text = entry.Arguments ?? "";
            name.Text = entry.Name;
            console = CoverSearch.Consoles.FirstOrDefault(c => c.Label == entry.Console) ?? console;
        }

        Width = 640;
        Margin = new Thickness(30, 26, 30, 26);
        AllowDrop = true;
        Drop += OnDrop;
        PreviewKeyDown += OnPreviewKeyDown;

        Children.Add(new TextBlock { Text = editing is null ? Loc.T("Add a game") : Loc.T("Edit game"), FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Brushes.White });

        var browse = Button(Loc.T("Browse..."), secondary: true, 100);
        browse.Click += (_, _) => PickProgram();
        Children.Add(Row(Loc.T("Program"), program, browse));
        program.TextChanged += (_, _) => UpdateEmulator();

        // Shown when the program is an emulator GameShelf knows: pick the game, the arguments are written for you.
        var browseGame = Button(Loc.T("Browse..."), secondary: true, 100);
        browseGame.Click += (_, _) => PickGameFile();
        emulatorRow = Row(Loc.T("Game file (ISO, ROM...)"), gameFile, browseGame);
        emulatorRow.Children.Add(emulatorHint);
        Children.Add(emulatorRow);
        UpdateEmulator(); // when editing, the program is already there
        Children.Add(Row(Loc.T("Arguments (optional)"), arguments));
        Children.Add(Row(Loc.T("Name"), name));

        Children.Add(Caption(Loc.T("Console")));
        foreach (var choice in CoverSearch.Consoles)
        {
            var chip = new ConsoleChip(choice.Label) { IsChecked = choice == console };
            chip.Click += (_, _) => SetConsole(choice);
            consoleChips.Children.Add(chip);
        }
        Children.Add(consoleChips);

        var find = Button(Loc.T("Search covers"), secondary: true, 130);
        find.Margin = new Thickness(0, 16, 10, 0);
        find.Click += async (_, _) => await SearchAsync();
        var own = Button(Loc.T("Choose an image..."), secondary: true, 150);
        own.Margin = new Thickness(0, 16, 0, 0);
        own.Click += (_, _) => PickPicture();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(find);
        buttons.Children.Add(own);
        Children.Add(buttons);

        status.Text = editing is null
            ? Loc.T("You can also drop or paste an image here. Without a cover, GameShelf draws one from the program's icon.")
            : Loc.T("The cover stays as it is unless you pick another one: search, choose, drop or paste an image.");
        Children.Add(status);
        Children.Add(new ScrollViewer { Content = covers, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Children.Add(error);

        var cancel = Button(Loc.T("Cancel"), secondary: true, 110);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 10, 0);
        cancel.Click += (_, _) => DialogHost.Close(this);
        var add = Button(editing is null ? Loc.T("Add") : Loc.T("Save"), secondary: false, 120);
        add.IsDefault = true;
        add.Click += (_, _) => Add();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        footer.Children.Add(cancel);
        footer.Children.Add(add);
        Children.Add(footer);
    }

    /// <summary>Opens the window to add a game, or to edit <paramref name="editing"/>. True when an edit was saved.</summary>
    public static bool Show(Window owner, Game? editing = null)
    {
        var dialog = new AddGameDialog(editing);
        DialogHost.ShowModal(owner, dialog);
        return dialog.Saved;
    }

    void PickProgram()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Programs") + "|*.exe;*.lnk;*.bat;*.cmd;*.url|" + Loc.T("All files") + "|*.*" };
        if (dialog.ShowDialog() == true) SetProgram(dialog.FileName);
    }

    /// <summary>
    /// A shortcut is read for what it starts and the arguments it passes (so what the user already set up in it is kept);
    /// anything else is the program itself.
    /// </summary>
    void SetProgram(string path)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShortcutFile.Resolve(path) is { } shortcut)
        {
            program.Text = File.Exists(shortcut.Target) ? shortcut.Target : path;
            if (arguments.Text.Length == 0) arguments.Text = shortcut.Arguments;
        }
        else
        {
            program.Text = path;
        }
        if (name.Text.Length == 0) name.Text = Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Shows the game file row when the program is an emulator that has a preset.</summary>
    void UpdateEmulator()
    {
        emulator = Emulators.Detect(program.Text.Trim().Trim('"'));
        emulatorRow.Visibility = emulator is null ? Visibility.Collapsed : Visibility.Visible;
        if (emulator is not null) emulatorHint.Text = Loc.T("{0} detected: pick the game file and GameShelf writes the arguments.", emulator.Name);
    }

    void PickGameFile()
    {
        if (emulator is null) return;
        var dialog = new OpenFileDialog { Filter = Loc.T("Games") + "|" + emulator.Extensions + "|" + Loc.T("All files") + "|*.*" };
        if (dialog.ShowDialog() != true) return;

        gameFile.Text = dialog.FileName;
        arguments.Text = emulator.ArgumentsFor(dialog.FileName);
        if (name.Text.Length == 0 || name.Text == Path.GetFileNameWithoutExtension(program.Text.Trim()))
            name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        if (CoverSearch.Consoles.FirstOrDefault(c => c.Label == emulator.Console) is { } known) SetConsole(known);
    }

    void SetConsole(ConsoleChoice choice)
    {
        console = choice;
        foreach (var chip in consoleChips.Children.OfType<ConsoleChip>()) chip.IsChecked = chip.Content as string == choice.Label;
    }

    void PickPicture()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Images") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif|" + Loc.T("All files") + "|*.*" };
        if (dialog.ShowDialog() == true) UsePicture(dialog.FileName);
    }

    void UsePicture(string path)
    {
        if (CoverImage.TryReadPicture(path) is { } picture) AddCandidate(Path.GetFileName(path), picture, select: true);
        else ShowError(Loc.T("This file is not a picture GameShelf can read."));
    }

    async Task SearchAsync()
    {
        if (name.Text.Trim().Length == 0)
        {
            ShowError(Loc.T("Give the game a name."));
            return;
        }
        error.Visibility = Visibility.Collapsed;
        status.Text = Loc.T("Searching...");
        var found = await CoverSearch.SearchAsync(name.Text.Trim(), console);
        foreach (var hit in found) AddCandidate(hit.Title, hit.Image, select: false);
        status.Text = found.Count > 0
            ? Loc.T("Click the cover to use. You can also drop or paste an image here.")
            : Loc.T("No cover found. Choose an image, or GameShelf draws one from the program's icon.");
    }

    void AddCandidate(string title, byte[] picture, bool select)
    {
        var image = new Image { Source = CoverImage.Decode(picture), Width = 96, Height = 144, Stretch = Stretch.UniformToFill };
        var caption = new TextBlock
        {
            Text = title,
            Foreground = Muted,
            FontSize = 10.5,
            Width = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = title,
            Margin = new Thickness(0, 3, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(image);
        stack.Children.Add(caption);
        var card = new Border
        {
            Child = stack,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
        };
        card.MouseLeftButtonUp += (_, _) => Choose(card, picture);
        candidates.Add((card, picture));
        covers.Children.Add(card);
        if (select) Choose(card, picture);
    }

    void Choose(Border card, byte[] picture)
    {
        chosen = picture;
        foreach (var (other, _) in candidates) other.BorderBrush = other == card ? Gold : Brushes.Transparent;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        // A program or a shortcut dropped here is the game's program; anything else is taken for a cover.
        if (Path.GetExtension(files[0]).ToLowerInvariant() is ".exe" or ".lnk" or ".bat" or ".cmd" or ".url") SetProgram(files[0]);
        else UsePicture(files[0]);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+V pastes a picture; text is left to the field that has the focus.
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control || Clipboard.ContainsText()) return;
        if (CoverImage.FromClipboard() is { } picture)
        {
            AddCandidate(Loc.T("Pasted image"), picture, select: true);
            e.Handled = true;
        }
    }

    void Add()
    {
        var exe = program.Text.Trim().Trim('"');
        var title = name.Text.Trim();
        if (!File.Exists(exe))
        {
            ShowError(Loc.T("Pick the program that starts the game."));
            return;
        }
        if (title.Length == 0)
        {
            ShowError(Loc.T("Give the game a name."));
            return;
        }

        if (editing is not null)
        {
            ManualGames.Edit(editing, title, exe, arguments.Text, console.Label, chosen is not null ? CoverImage.FromPicture(chosen) : null);
            Saved = true;
        }
        else
        {
            ManualGames.Add(title, exe, arguments.Text, console.Label, chosen is not null ? CoverImage.FromPicture(chosen) : CoverImage.Generate(title, exe));
        }
        DialogHost.Close(this);
    }

    void ShowError(string text)
    {
        error.Text = text;
        error.Visibility = Visibility.Visible;
    }

    static TextBox Field() => new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Brushes.White,
        CaretBrush = Gold,
        FontSize = 14,
        Padding = new Thickness(10, 8, 10, 8),
        MaxLength = 400,
    };

    static TextBlock Caption(string text) => new() { Text = text, Foreground = Muted, FontSize = 12.5, Margin = new Thickness(0, 16, 0, 0) };

    /// <summary>A caption over a field, with an optional button at its right.</summary>
    static StackPanel Row(string label, TextBox field, Button? side = null)
    {
        var holder = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        holder.ColumnDefinitions.Add(new ColumnDefinition());
        holder.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1d, 0x1a, 0x17)), BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = field };
        holder.Children.Add(box);
        if (side is not null)
        {
            side.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(side, 1);
            holder.Children.Add(side);
        }
        var row = new StackPanel();
        row.Children.Add(Caption(label));
        row.Children.Add(holder);
        return row;
    }

    static Button Button(string text, bool secondary, double minWidth) => new()
    {
        Content = text,
        Style = (Style)Application.Current.FindResource(secondary ? "SecondaryButton" : "PrimaryButton"),
        Height = 38,
        MinWidth = minWidth,
    };

    /// <summary>A console to pick: the app's chip look.</summary>
    sealed class ConsoleChip : System.Windows.Controls.Primitives.ToggleButton
    {
        public ConsoleChip(string label)
        {
            Content = label;
            Style = (Style)Application.Current.FindResource("ChipToggle");
            Margin = new Thickness(0, 0, 6, 6);
        }
    }
}
