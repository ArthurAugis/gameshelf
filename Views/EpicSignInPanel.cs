using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GameShelf.Launchers;
using GameShelf.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace GameShelf.Views;

/// <summary>
/// Epic's sign-in page inside GameShelf (the system's WebView2), shown over the main window. When Epic redirects to
/// the page that shows the authorization code, the browser hides itself, the code is read from the page and the panel
/// closes: the user never sees it. The browser keeps nothing: its data folder is a temporary one, deleted afterwards.
/// </summary>
internal sealed class EpicSignInPanel : Grid
{
    const string RedirectPath = "/id/api/redirect";

    readonly string dataFolder = Path.Combine(Path.GetTempPath(), "GameShelf-webview-" + Guid.NewGuid().ToString("N"));
    string? code;
    bool available = true;

    WebView2 Browser => (WebView2)Children[1];

    EpicSignInPanel()
    {
        Width = 520;
        Height = Math.Min(700, SystemParameters.WorkArea.Height - 120); // a small screen gets a shorter panel
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x16, 0x14));
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        RowDefinitions.Add(new RowDefinition());

        var title = new TextBlock
        {
            Text = Loc.T("Sign in to Epic Games"),
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 0, 0),
        };
        var close = new Button
        {
            Content = "", // the cross of Segoe MDL2 Assets
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Style = (Style)Application.Current.FindResource("LinkButton"),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 0, 16, 0),
            ToolTip = Loc.T("Close"),
        };
        close.Click += (_, _) => DialogHost.Close(this);
        var bar = new Grid { Children = { title, close } };
        SetRow(bar, 0);
        var browser = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(24, 22, 20) };
        SetRow(browser, 1);
        Children.Add(bar);
        Children.Add(browser);

        Loaded += async (_, _) => await StartAsync();
        Unloaded += (_, _) =>
        {
            Browser.Dispose();
            try
            {
                Directory.Delete(dataFolder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still held by the browser process for a moment: the temporary folder stays until Windows cleans it.
            }
        };
    }

    /// <summary>
    /// Shows the sign-in page and returns the authorization code once the user has signed in, or null if they closed
    /// the panel. <paramref name="browserAvailable"/> is false when WebView2 is not installed on this PC.
    /// </summary>
    public static string? Ask(Window owner, out bool browserAvailable)
    {
        var panel = new EpicSignInPanel();
        DialogHost.ShowModal(owner, panel);
        browserAvailable = panel.available;
        return panel.code;
    }

    async Task StartAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or InvalidOperationException or COMException)
        {
            available = false;
            DialogHost.Close(this);
            return;
        }

        var web = Browser.CoreWebView2;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        web.NavigationStarting += (_, e) =>
        {
            if (IsRedirect(e.Uri)) Browser.Visibility = Visibility.Hidden; // the page is a JSON answer, not for the user
        };
        web.NavigationCompleted += async (_, _) =>
        {
            if (IsRedirect(web.Source)) await ReadCodeAsync(web);
        };
        web.Navigate(EpicClient.SignInUrl);
    }

    async Task ReadCodeAsync(CoreWebView2 web)
    {
        try
        {
            var text = JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("document.body.innerText"));
            code = EpicClient.ExtractCode(text ?? "");
        }
        catch (Exception e) when (e is JsonException or EpicException or COMException)
        {
            code = null;
        }
        DialogHost.Close(this);
    }

    static bool IsRedirect(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Host.EndsWith("epicgames.com", StringComparison.Ordinal)
        && parsed.AbsolutePath.StartsWith(RedirectPath, StringComparison.Ordinal);
}
