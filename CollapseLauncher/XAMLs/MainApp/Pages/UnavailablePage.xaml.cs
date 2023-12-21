using Microsoft.UI.Xaml.Controls;

namespace CollapseLauncher.Pages
{
    public sealed partial class UnavailablePage : Page
    {
        public UnavailablePage()
        {
            BackgroundAssetChanger.ToggleBackground(true);
            this.InitializeComponent();
        }
    }
}
