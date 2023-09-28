using Microsoft.UI.Xaml.Controls;

namespace CollapseLauncher.Pages
{
    public sealed partial class UnavailablePage : Page
    {
        public UnavailablePage()
        {
            BackgroundMediaChanger.ToggleBackground(true);
            this.InitializeComponent();
        }
    }
}
