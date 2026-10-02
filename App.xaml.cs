using System.Windows;
using GameShelf.Services;

namespace GameShelf;

internal sealed partial class App : Application
{
    public App() => Loc.Initialize();
}
