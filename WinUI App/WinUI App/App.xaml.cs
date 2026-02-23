using Microsoft.UI.Xaml;
using WinUI_App.Services;
using System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUI_App
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        public static MainWindow? MainWindowInstance { get; private set; }
        public static TrayHotkeyController? TrayHotkeys { get; private set; }

        public static AppSettings Settings { get; } = new AppSettings();
        public static ToastService Toasts { get; } = new ToastService();
        public static RecordingController Recording { get; } = new RecordingController(GetReportsRootFolder());

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            MainWindowInstance = _window as MainWindow;

            Toasts.Initialize();
            Toasts.ToastInvoked += _ => MainWindowInstance?.RestoreAndFocus();

            if (MainWindowInstance != null)
            {
                try
                {
                    TrayHotkeys = new TrayHotkeyController(MainWindowInstance);
                }
                catch (Exception ex)
                {
                    DebugLog.Warn($"Tray/hotkeys init failed: {ex.Message}");
                }
            }

            _window.Activate();
        }
        
        public static SupabaseAuthService? SharedAuthService { get; set; }

        private static string GetReportsRootFolder()
        {
            var root = System.IO.Path.Combine(AppContext.BaseDirectory, "Reports");
            System.IO.Directory.CreateDirectory(root);
            return root;
        }
    }
}
