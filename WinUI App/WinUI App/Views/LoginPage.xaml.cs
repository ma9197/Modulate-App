using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinUI_App.Services;

namespace WinUI_App.Views
{
    public sealed partial class LoginPage : Page
    {
        private readonly SupabaseAuthService _authService;

        public LoginPage()
        {
            this.InitializeComponent();
            _authService = App.SharedAuthService ?? new SupabaseAuthService();
            App.SharedAuthService = _authService;
            
            // Check if already logged in
            if (_authService.IsAuthenticated)
            {
                NavigateToMainPage();
            }
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            await PerformLoginAsync();
        }

        private async void SignUpButton_Click(object sender, RoutedEventArgs e)
        {
            await PerformSignUpAsync();
        }

        private void InputKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _ = PerformLoginAsync();
            }
        }

        private async System.Threading.Tasks.Task PerformLoginAsync()
        {
            var email = EmailTextBox.Text.Trim();
            var password = PasswordBox.Password;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                ShowMessage("Please enter both email and password", InfoBarSeverity.Warning);
                return;
            }

            SetLoading(true);
            MessageBar.IsOpen = false;

            var (success, error) = await _authService.LoginAsync(email, password);

            SetLoading(false);

            if (success)
            {
                ShowMessage($"Welcome back, {_authService.CurrentUser?.Email}!", InfoBarSeverity.Success);
                
                // Navigate to main page after short delay
                await System.Threading.Tasks.Task.Delay(500);
                NavigateToMainPage();
            }
            else
            {
                ShowMessage($"Login failed: {error}", InfoBarSeverity.Error);
            }
        }

        private async System.Threading.Tasks.Task PerformSignUpAsync()
        {
            var email = EmailTextBox.Text.Trim();
            var password = PasswordBox.Password;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                ShowMessage("Please enter both email and password", InfoBarSeverity.Warning);
                return;
            }

            if (password.Length < 6)
            {
                ShowMessage("Password must be at least 6 characters", InfoBarSeverity.Warning);
                return;
            }

            SetLoading(true);
            MessageBar.IsOpen = false;

            var (success, error) = await _authService.SignUpAsync(email, password);

            SetLoading(false);

            if (success)
            {
                ShowMessage($"Account created! Welcome, {_authService.CurrentUser?.Email}!", InfoBarSeverity.Success);
                
                // Navigate to main page after short delay
                await System.Threading.Tasks.Task.Delay(500);
                NavigateToMainPage();
            }
            else
            {
                ShowMessage($"Sign up failed: {error}", InfoBarSeverity.Error);
            }
        }

        private void ShowMessage(string message, InfoBarSeverity severity)
        {
            MessageBar.Message = message;
            MessageBar.Severity = severity;
            MessageBar.IsOpen = true;
        }

        private void SetLoading(bool isLoading)
        {
            LoadingRing.IsActive = isLoading;
            LoginButton.IsEnabled = !isLoading;
            SignUpButton.IsEnabled = !isLoading;
            EmailTextBox.IsEnabled = !isLoading;
            PasswordBox.IsEnabled = !isLoading;
        }

        private void NavigateToMainPage()
        {
            // Pass the auth service instance to the main page
            Frame.Navigate(typeof(MainPage), _authService);
        }
    }
}

