using System;
using System.Linq;

namespace WinUI_App.Services
{
    /// <summary>
    /// Generates unique, traceable media file names.
    /// Format: {yyyyMMdd_HHmmss}_{6-char-id}
    /// Examples:
    ///   Storage (different buckets, no type prefix needed):
    ///     20260222_143045_a3f7x2.wav
    ///   Local (same folder, type prefix added for clarity):
    ///     desktop_20260222_143045_a3f7x2.wav
    ///     mic_20260222_143045_k9m1p4.wav
    ///     video_20260222_143045_m8n3q1.avi
    /// </summary>
    public static class MediaFileNamer
    {
        private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        private static readonly Random _rng = new();

        /// <summary>
        /// Generates a random lowercase alphanumeric ID of exactly <paramref name="length"/> characters.
        /// </summary>
        public static string GenerateId(int length = 6)
        {
            return new string(Enumerable.Range(0, length)
                .Select(_ => Alphabet[_rng.Next(Alphabet.Length)])
                .ToArray());
        }

        /// <summary>
        /// Core name builder: <c>{yyyyMMdd_HHmmss}_{id}{ext}</c>.
        /// Used for Supabase storage paths (files live in different buckets so no type prefix is needed).
        /// </summary>
        public static string ForStorage(string extension, DateTime? at = null, string? id = null)
        {
            var ts = (at ?? DateTime.UtcNow).ToString("yyyyMMdd_HHmmss");
            id ??= GenerateId();
            return $"{ts}_{id}{extension}";
        }

        // ── Local file names (prefix distinguishes type in the same folder) ──────────

        /// <summary>Desktop / system-loopback audio file for local storage.</summary>
        public static string DesktopAudio(DateTime? at = null, string? id = null)
            => $"desktop_{ForStorage(".wav", at, id)}";

        /// <summary>Microphone audio file for local storage.</summary>
        public static string MicAudio(DateTime? at = null, string? id = null)
            => $"mic_{ForStorage(".wav", at, id)}";

        /// <summary>Video capture file for local storage.</summary>
        public static string Video(DateTime? at = null, string? id = null)
            => $"video_{ForStorage(".avi", at, id)}";
    }
}
