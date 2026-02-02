using Microsoft.UI.Xaml;
using WinUI_App.Views;

namespace WinUI_App
{
    /// <summary>
    /// Main application window with navigation frame
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            
            // Set window size
            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
            
            // Navigate to login page on startup
            RootFrame.Navigate(typeof(LoginPage));
        }
    }
}
