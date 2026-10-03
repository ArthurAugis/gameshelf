using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GameShelf.Controls;
using GameShelf.Launchers;
using GameShelf.Models;
using GameShelf.Services;
using GameShelf.Theming;

namespace GameShelf.Views;

/// <summary>The main window: language and shelf skins.</summary>
internal sealed partial class MainWindow : Window
{
    // ---- Language ----

    void OnLanguageClick(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow - languagePopupClosedAt < TimeSpan.FromMilliseconds(250)) return; // see OnFiltersClick
        LanguagePopup.IsOpen = !LanguagePopup.IsOpen;
    }

    void OnLanguagePopupClosed(object? sender, EventArgs e) => languagePopupClosedAt = DateTime.UtcNow;

    void BuildLanguageChips()
    {
        foreach (var language in Loc.Languages)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(Flags.Create(language.Code, 13));
            label.Children.Add(new TextBlock { Text = language.NativeName, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var chip = new ToggleButton { Content = label, Style = ChipStyle, IsChecked = language == Loc.Current };
            chip.Click += (_, _) => ChooseLanguage(language);
            LanguageChips.Children.Add(chip);
        }
    }

    /// <summary>Saves the language. The texts are read when the windows are built, so the app restarts to apply it.</summary>
    void ChooseLanguage(Language language)
    {
        LanguagePopup.IsOpen = false;
        if (language == Loc.Current) return;

        Loc.Save(language);
        // The dialog is shown in the language being left: it tells the user what happens next in the new one.
        if (!MessageDialog.Confirm(this, Loc.T("Restart GameShelf?"),
                Loc.T("GameShelf restarts to switch to {0}.", language.NativeName), Loc.T("Restart"), Loc.T("Later")))
            return;

        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    // ---- Skins ----

    /// <summary>One round texture chip per skin in the header, and a "+" chip to import a texture.</summary>
    void BuildSkinChips()
    {
        SkinChips.Children.Clear();
        foreach (var name in Skins.Names) SkinChips.Children.Add(CreateSkinChip(name));

        var add = CreateChip(null, Loc.T("Import a texture  ·  a picture of wood, stone, fabric..."));
        add.Background = Brushes.Transparent;
        add.Child = new TextBlock
        {
            Text = "+",
            Foreground = new SolidColorBrush(Color.FromArgb(0xb0, 255, 255, 255)),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0),
        };
        add.MouseLeftButtonUp += (_, _) => ImportSkin();
        SkinChips.Children.Add(add);
    }

    Border CreateSkinChip(string name)
    {
        var chip = CreateChip(name, Skins.IsImported(name) ? Loc.T("{0}  ·  right-click to delete", name) : Loc.T(name));
        chip.Background = Skins.Get(name).Plank;
        chip.MouseLeftButtonUp += (_, _) => SelectSkin(name);
        if (Skins.IsImported(name)) chip.MouseRightButtonUp += (_, _) => DeleteSkin(name);
        return chip;
    }

    /// <summary>A round chip. <paramref name="skinName"/> goes in Tag, so the selected one can be found again.</summary>
    static Border CreateChip(string? skinName, string tooltip) => new()
    {
        Width = 24,
        Height = 24,
        CornerRadius = new CornerRadius(12),
        Margin = new Thickness(4, 0, 0, 0),
        BorderThickness = new Thickness(2),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 255, 255, 255)),
        Cursor = Cursors.Hand,
        Tag = skinName,
        ToolTip = tooltip,
    };

    void SelectSkin(string name)
    {
        skinName = name;
        ApplySkin();
        RebuildShelf();
        AppData.WriteText(SkinSetting, name);
    }

    void ImportSkin()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Choose a picture to use as the shelf texture"),
            Filter = $"{Loc.T("Pictures")}|*.png;*.jpg;*.jpeg;*.bmp",
        };
        if (picker.ShowDialog(this) != true) return;

        var name = InputDialog.Ask(this, Loc.T("Name this shelf"), Loc.T("Add"), Skins.Validate,
            initialText: System.IO.Path.GetFileNameWithoutExtension(picker.FileName));
        if (name is null) return;

        try
        {
            Skins.Import(name, picker.FileName);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageDialog.Info(this, Loc.T("Could not import the texture"), e.Message);
            return;
        }
        BuildSkinChips();
        SelectSkin(name);
    }

    void DeleteSkin(string name)
    {
        if (!MessageDialog.Confirm(this, Loc.T("Delete the shelf \"{0}\"?", name),
                Loc.T("Its texture is removed from GameShelf. Your original picture is not touched."), Loc.T("Delete"),
                destructive: true))
            return;

        Skins.Delete(name);
        BuildSkinChips();
        SelectSkin(skinName == name ? Skins.Names[0] : skinName);
    }

    void ApplySkin()
    {
        skin = Skins.Get(skinName);
        ShelfScroll.Background = skin.Wall;

        var selected = new SolidColorBrush(Color.FromRgb(0xd9, 0xb7, 0x7a));
        var unselected = new SolidColorBrush(Color.FromArgb(0x44, 255, 255, 255));
        foreach (var chip in SkinChips.Children.OfType<Border>().Where(chip => chip.Tag is not null))
            chip.BorderBrush = (string)chip.Tag == skinName ? selected : unselected;
    }
}
