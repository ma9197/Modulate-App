using System;
using System.Linq;
using Windows.System;

namespace WinUI_App.Models
{
    [Flags]
    public enum HotkeyModifiers : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008
    }

    public readonly record struct HotkeyBinding(HotkeyModifiers Modifiers, VirtualKey Key, bool Enabled = true)
    {
        public override string ToString()
        {
            if (!Enabled)
            {
                return $"{ToDisplayString(Modifiers, Key)} (disabled)";
            }
            return ToDisplayString(Modifiers, Key);
        }

        public static string ToDisplayString(HotkeyModifiers modifiers, VirtualKey key)
        {
            var parts = new System.Collections.Generic.List<string>(4);
            if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(KeyToDisplay(key));
            return string.Join("+", parts);
        }

        private static string KeyToDisplay(VirtualKey key)
        {
            // Common friendly formatting; fall back to enum name.
            if (key is >= VirtualKey.A and <= VirtualKey.Z)
            {
                return ((char)('A' + (key - VirtualKey.A))).ToString();
            }
            if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
            {
                return ((char)('0' + (key - VirtualKey.Number0))).ToString();
            }
            return key.ToString();
        }

        public static bool TryParse(string? text, out HotkeyBinding binding)
        {
            binding = default;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var t = text.Trim();
            var disabled = false;
            if (t.EndsWith("(disabled)", StringComparison.OrdinalIgnoreCase))
            {
                disabled = true;
                t = t.Substring(0, t.Length - "(disabled)".Length).Trim();
            }

            var parts = t.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            HotkeyModifiers mods = HotkeyModifiers.None;
            VirtualKey key = VirtualKey.None;

            foreach (var p in parts)
            {
                var s = p.Trim();
                if (s.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || s.Equals("control", StringComparison.OrdinalIgnoreCase))
                {
                    mods |= HotkeyModifiers.Control;
                }
                else if (s.Equals("shift", StringComparison.OrdinalIgnoreCase))
                {
                    mods |= HotkeyModifiers.Shift;
                }
                else if (s.Equals("alt", StringComparison.OrdinalIgnoreCase))
                {
                    mods |= HotkeyModifiers.Alt;
                }
                else if (s.Equals("win", StringComparison.OrdinalIgnoreCase) || s.Equals("windows", StringComparison.OrdinalIgnoreCase))
                {
                    mods |= HotkeyModifiers.Win;
                }
                else
                {
                    // key token
                    if (s.Length == 1 && char.IsLetter(s[0]))
                    {
                        var c = char.ToUpperInvariant(s[0]);
                        key = VirtualKey.A + (c - 'A');
                    }
                    else if (s.Length == 1 && char.IsDigit(s[0]))
                    {
                        var c = s[0];
                        key = VirtualKey.Number0 + (c - '0');
                    }
                    else if (Enum.TryParse<VirtualKey>(s, ignoreCase: true, out var parsed))
                    {
                        key = parsed;
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            if (key == VirtualKey.None)
            {
                return false;
            }

            binding = new HotkeyBinding(mods, key, Enabled: !disabled);
            return true;
        }
    }
}


