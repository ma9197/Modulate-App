using System;
using Windows.Storage;
using Windows.System;
using WinUI_App.Models;

namespace WinUI_App.Services
{
    public class AppSettings
    {
        private readonly ApplicationDataContainer _container;

        public AppSettings()
        {
            _container = ApplicationData.Current.LocalSettings;
        }

        public bool MinimizeToTray
        {
            get => GetBool(nameof(MinimizeToTray), defaultValue: true);
            set => SetBool(nameof(MinimizeToTray), value);
        }

        public bool CloseButtonExitsApp
        {
            get => GetBool(nameof(CloseButtonExitsApp), defaultValue: false);
            set => SetBool(nameof(CloseButtonExitsApp), value);
        }

        public bool HasShownRunningInBackgroundToast
        {
            get => GetBool(nameof(HasShownRunningInBackgroundToast), defaultValue: false);
            set => SetBool(nameof(HasShownRunningInBackgroundToast), value);
        }

        public bool OverlayEnabled
        {
            get => GetBool(nameof(OverlayEnabled), defaultValue: true);
            set => SetBool(nameof(OverlayEnabled), value);
        }

        public string OverlayPosition
        {
            get => GetString(nameof(OverlayPosition), "TopRight");
            set => SetString(nameof(OverlayPosition), value);
        }

        public HotkeyBinding HotkeyStartStop
        {
            get => GetHotkey(nameof(HotkeyStartStop), new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.R, Enabled: true));
            set => SetHotkey(nameof(HotkeyStartStop), value);
        }

        public HotkeyBinding HotkeyFlag
        {
            get => GetHotkey(nameof(HotkeyFlag), new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.F, Enabled: true));
            set => SetHotkey(nameof(HotkeyFlag), value);
        }

        public HotkeyBinding HotkeyOpenApp
        {
            get => GetHotkey(nameof(HotkeyOpenApp), new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKey.O, Enabled: false));
            set => SetHotkey(nameof(HotkeyOpenApp), value);
        }

        /// <summary>
        /// Preferred microphone device number (NAudio WaveIn index).
        /// -1 means "default system microphone".
        /// </summary>
        public int PreferredMicrophoneDeviceNumber
        {
            get => GetInt(nameof(PreferredMicrophoneDeviceNumber), -1);
            set => SetInt(nameof(PreferredMicrophoneDeviceNumber), value);
        }

        private bool GetBool(string key, bool defaultValue)
        {
            try
            {
                if (_container.Values.TryGetValue(key, out var val) && val is bool b)
                {
                    return b;
                }
            }
            catch { }
            return defaultValue;
        }

        private void SetBool(string key, bool value)
        {
            try { _container.Values[key] = value; } catch { }
        }

        private HotkeyBinding GetHotkey(string key, HotkeyBinding defaultValue)
        {
            try
            {
                if (_container.Values.TryGetValue(key, out var val) && val is string s && HotkeyBinding.TryParse(s, out var parsed))
                {
                    return parsed;
                }
            }
            catch { }

            return defaultValue;
        }

        private void SetHotkey(string key, HotkeyBinding value)
        {
            try
            {
                _container.Values[key] = HotkeyBinding.ToDisplayString(value.Modifiers, value.Key) + (value.Enabled ? "" : " (disabled)");
            }
            catch { }
        }

        private string GetString(string key, string defaultValue)
        {
            try
            {
                if (_container.Values.TryGetValue(key, out var val) && val is string s && !string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
            catch { }
            return defaultValue;
        }

        private void SetString(string key, string value)
        {
            try { _container.Values[key] = value; } catch { }
        }

        private int GetInt(string key, int defaultValue)
        {
            try
            {
                if (_container.Values.TryGetValue(key, out var val))
                {
                    if (val is int i) return i;
                    if (val is string s && int.TryParse(s, out var parsed)) return parsed;
                }
            }
            catch { }
            return defaultValue;
        }

        private void SetInt(string key, int value)
        {
            try { _container.Values[key] = value; } catch { }
        }
    }
}


