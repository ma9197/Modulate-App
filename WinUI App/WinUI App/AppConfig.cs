using System;
using System.IO;
using System.Text.Json;

namespace WinUI_App
{
    /// <summary>
    /// Application configuration loaded from appsettings.json
    /// </summary>
    public class AppConfig
    {
        public string SupabaseUrl { get; set; } = string.Empty;
        public string SupabaseAnonKey { get; set; } = string.Empty;
        public string WorkerUrl { get; set; } = string.Empty;

        private static AppConfig? _instance;
        private static readonly object _lock = new object();

        /// <summary>
        /// Singleton instance of configuration
        /// </summary>
        public static AppConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = LoadConfiguration();
                        }
                    }
                }
                return _instance;
            }
        }

        private static AppConfig LoadConfiguration()
        {
            try
            {
                var appDirectory = AppContext.BaseDirectory;
                var configPath = Path.Combine(appDirectory, "appsettings.json");

                if (!File.Exists(configPath))
                {
                    // Return default config if file doesn't exist
                    return new AppConfig
                    {
                        SupabaseUrl = "https://project-id.supabase.co",
                        SupabaseAnonKey = "ANON_KEY_HERE",
                        WorkerUrl = "http://localhost:8787"
                    };
                }

                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                return config ?? new AppConfig();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load configuration: {ex.Message}");
                return new AppConfig();
            }
        }
    }
}

