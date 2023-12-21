using Microsoft.UI.Xaml.Controls;

namespace CollapseLauncher.Pages
{
    public sealed partial class NotInstalledPage : Page
    {
        public NotInstalledPage()
        {
            BackgroundAssetChanger.ToggleBackground(true);
            this.InitializeComponent();
        }
    }
}
