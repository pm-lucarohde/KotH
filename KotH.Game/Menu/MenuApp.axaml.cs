using Avalonia;
using Avalonia.Markup.Xaml;

namespace KotH.Client.Menu
{
    /// <summary>
    /// The Avalonia application behind the main menu: Fluent's controls under
    /// the menu's own look (MenuTheme.axaml). Rendered headless, see MenuOverlay.
    /// </summary>
    public partial class MenuApp : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);
    }
}
