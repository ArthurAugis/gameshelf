using System.Windows;
using System.Windows.Controls;
using GameShelf.Services;

namespace GameShelf.Views;

internal enum ExactModeChoice { NotNow, Enable, Never }

/// <summary>Explains why Steam's debug mode is needed for an exact library, and asks the user to turn it on.</summary>
internal sealed partial class ExactModeDialog : UserControl
{
    public ExactModeDialog()
    {
        InitializeComponent();
        Loc.Apply(this);
    }

    public ExactModeChoice Choice { get; private set; } = ExactModeChoice.NotNow;

    void OnEnableClick(object sender, RoutedEventArgs e) => Finish(ExactModeChoice.Enable);

    void OnNotNowClick(object sender, RoutedEventArgs e) => Finish(ExactModeChoice.NotNow);

    void OnNeverClick(object sender, RoutedEventArgs e) => Finish(ExactModeChoice.Never);

    void Finish(ExactModeChoice choice)
    {
        Choice = choice;
        DialogHost.Close(this);
    }
}
