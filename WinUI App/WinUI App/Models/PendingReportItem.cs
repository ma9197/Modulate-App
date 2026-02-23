using System;
using System.Text.Json.Serialization;

namespace WinUI_App.Models
{
    public class PendingReportItem
    {
        public string Id { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime FlagUtc { get; set; }
        public DateTime RecordingStartUtc { get; set; }
        public string? GameName { get; set; }
        public string? OffenderName { get; set; }
        public string? Description { get; set; }
        public bool Targeted { get; set; }
        public string? DesiredAction { get; set; }
        public string? AudioPath { get; set; }
        public string? MicrophonePath { get; set; }
        public string? VideoPath { get; set; }

        [JsonIgnore]
        public string DisplayTitle
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Description))
                {
                    return Description.Length > 60 ? Description.Substring(0, 60) + "..." : Description;
                }
                return "Pending report";
            }
        }

        [JsonIgnore]
        public string DisplayTime => CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        [JsonIgnore]
        public string DisplayDetails
        {
            get
            {
                var game = string.IsNullOrWhiteSpace(GameName) ? null : $"Game: {GameName}";
                var offender = string.IsNullOrWhiteSpace(OffenderName) ? null : $"Offender: {OffenderName}";
                if (game == null && offender == null)
                {
                    return "No game or offender details";
                }
                if (game != null && offender != null)
                {
                    return $"{game} • {offender}";
                }
                return game ?? offender ?? string.Empty;
            }
        }
    }
}

