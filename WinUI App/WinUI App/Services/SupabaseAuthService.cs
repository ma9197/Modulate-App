using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using WinUI_App.Models;

namespace WinUI_App.Services
{
    /// <summary>
    /// Service for handling Supabase authentication
    /// </summary>
    public class SupabaseAuthService
    {
        private readonly HttpClient _httpClient;
        private readonly string _supabaseUrl;
        private readonly string _anonKey;
        private const string CREDENTIAL_RESOURCE = "ToxicityReporter_Auth";

        private string? _accessToken;
        private string? _refreshToken;
        private SupabaseUser? _currentUser;

        public SupabaseAuthService()
        {
            _httpClient = new HttpClient();
            _supabaseUrl = AppConfig.Instance.SupabaseUrl;
            _anonKey = AppConfig.Instance.SupabaseAnonKey;
            
            // Try to load saved credentials
            LoadSavedCredentials();
        }

        /// <summary>
        /// Get the current access token
        /// </summary>
        public string? AccessToken => _accessToken;

        /// <summary>
        /// Get the current user
        /// </summary>
        public SupabaseUser? CurrentUser => _currentUser;

        /// <summary>
        /// Check if user is authenticated
        /// </summary>
        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) && _currentUser != null;

        /// <summary>
        /// Sign up a new user
        /// </summary>
        public async Task<(bool success, string error)> SignUpAsync(string email, string password)
        {
            try
            {
                var request = new SignupRequest
                {
                    Email = email,
                    Password = password
                };

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/auth/v1/signup");
                httpRequest.Headers.Add("apikey", _anonKey);
                httpRequest.Content = JsonContent.Create(request);

                var response = await _httpClient.SendAsync(httpRequest);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var error = JsonSerializer.Deserialize<SupabaseErrorResponse>(responseContent);
                    return (false, error?.ErrorDescription ?? error?.Message ?? "Signup failed");
                }

                var authResponse = JsonSerializer.Deserialize<AuthResponse>(responseContent);
                if (authResponse != null)
                {
                    _accessToken = authResponse.AccessToken;
                    _refreshToken = authResponse.RefreshToken;
                    _currentUser = authResponse.User;
                    SaveCredentials();
                    return (true, string.Empty);
                }

                return (false, "Invalid response from server");
            }
            catch (Exception ex)
            {
                return (false, $"Network error: {ex.Message}");
            }
        }

        /// <summary>
        /// Log in an existing user
        /// </summary>
        public async Task<(bool success, string error)> LoginAsync(string email, string password)
        {
            try
            {
                var request = new LoginRequest
                {
                    Email = email,
                    Password = password
                };

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_supabaseUrl}/auth/v1/token?grant_type=password");
                httpRequest.Headers.Add("apikey", _anonKey);
                httpRequest.Content = JsonContent.Create(request);

                var response = await _httpClient.SendAsync(httpRequest);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var error = JsonSerializer.Deserialize<SupabaseErrorResponse>(responseContent);
                    return (false, error?.ErrorDescription ?? error?.Message ?? "Login failed");
                }

                var authResponse = JsonSerializer.Deserialize<AuthResponse>(responseContent);
                if (authResponse != null)
                {
                    _accessToken = authResponse.AccessToken;
                    _refreshToken = authResponse.RefreshToken;
                    _currentUser = authResponse.User;
                    SaveCredentials();
                    return (true, string.Empty);
                }

                return (false, "Invalid response from server");
            }
            catch (Exception ex)
            {
                return (false, $"Network error: {ex.Message}");
            }
        }

        /// <summary>
        /// Log out the current user
        /// </summary>
        public void Logout()
        {
            _accessToken = null;
            _refreshToken = null;
            _currentUser = null;
            ClearSavedCredentials();
        }

        private void SaveCredentials()
        {
            try
            {
                var vault = new PasswordVault();
                
                // Remove old credential if exists
                try
                {
                    var oldCred = vault.Retrieve(CREDENTIAL_RESOURCE, "auth_data");
                    vault.Remove(oldCred);
                }
                catch { }

                // Save new credential
                var credentialData = JsonSerializer.Serialize(new
                {
                    AccessToken = _accessToken,
                    RefreshToken = _refreshToken,
                    User = _currentUser
                });

                vault.Add(new PasswordCredential(CREDENTIAL_RESOURCE, "auth_data", credentialData));
            }
            catch { }
        }

        private void LoadSavedCredentials()
        {
            try
            {
                var vault = new PasswordVault();
                var credential = vault.Retrieve(CREDENTIAL_RESOURCE, "auth_data");
                credential.RetrievePassword();

                var credentialData = JsonSerializer.Deserialize<SavedCredentials>(credential.Password);
                if (credentialData != null)
                {
                    _accessToken = credentialData.AccessToken;
                    _refreshToken = credentialData.RefreshToken;
                    _currentUser = credentialData.User;
                }
            }
            catch { }
        }

        private void ClearSavedCredentials()
        {
            try
            {
                var vault = new PasswordVault();
                var credential = vault.Retrieve(CREDENTIAL_RESOURCE, "auth_data");
                vault.Remove(credential);
            }
            catch { }
        }

        private class SavedCredentials
        {
            public string? AccessToken { get; set; }
            public string? RefreshToken { get; set; }
            public SupabaseUser? User { get; set; }
        }
    }
}

