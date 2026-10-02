using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameShelf.Services;

namespace GameShelf.Controls;

/// <summary>A rounded search field with a placeholder and a clear button.</summary>
internal sealed partial class SearchBox : UserControl
{
    static readonly Brush IdleBorder = new SolidColorBrush(Color.FromRgb(0x3a, 0x35, 0x2f));
    static readonly Brush FocusedBorder = new SolidColorBrush(Color.FromRgb(0xd9, 0xb7, 0x7a));

    public SearchBox()
    {
        InitializeComponent();
        Loc.Apply(this);
    }

    public event Action? TextChanged;

    public string Text
    {
        get => Input.Text;
        set => Input.Text = value;
    }

    public void FocusInput()
    {
        Input.Focus();
        Input.SelectAll();
    }

    void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = Input.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TextChanged?.Invoke();
    }

    void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        Frame.BorderBrush = Input.IsKeyboardFocused ? FocusedBorder : IdleBorder;

    void OnClearClick(object sender, RoutedEventArgs e)
    {
        Input.Clear();
        Input.Focus();
    }
}
