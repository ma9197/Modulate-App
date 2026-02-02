using System.Text.Json.Serialization;

namespace WinUI_App.Models
{
    /// <summary>
    /// Request body for report metadata
    /// </summary>
    public class CreateReportRequest
    {
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("targeted")]
        public bool? Targeted { get; set; }

        [JsonPropertyName("desired_action")]
        public string? DesiredAction { get; set; }

        [JsonPropertyName("recording_start_utc")]
        public string? RecordingStartUtc { get; set; }

        [JsonPropertyName("flag_utc")]
        public string? FlagUtc { get; set; }

        [JsonPropertyName("clip_start_offset_sec")]
        public double? ClipStartOffsetSec { get; set; }

        [JsonPropertyName("clip_end_offset_sec")]
        public double? ClipEndOffsetSec { get; set; }
    }

    /// <summary>
    /// Initialize a report and request signed upload URLs
    /// </summary>
    public class ReportInitRequest
    {
        [JsonPropertyName("has_microphone")]
        public bool HasMicrophone { get; set; }

        [JsonPropertyName("has_video")]
        public bool HasVideo { get; set; }
    }

    /// <summary>
    /// Response for report initialization
    /// </summary>
    public class ReportInitResponse
    {
        [JsonPropertyName("report_id")]
        public string ReportId { get; set; } = string.Empty;

        [JsonPropertyName("audio_upload_url")]
        public string AudioUploadUrl { get; set; } = string.Empty;

        [JsonPropertyName("audio_upload_token")]
        public string AudioUploadToken { get; set; } = string.Empty;

        [JsonPropertyName("audio_path")]
        public string AudioPath { get; set; } = string.Empty;

        [JsonPropertyName("microphone_upload_url")]
        public string? MicrophoneUploadUrl { get; set; }

        [JsonPropertyName("microphone_upload_token")]
        public string? MicrophoneUploadToken { get; set; }

        [JsonPropertyName("microphone_path")]
        public string? MicrophonePath { get; set; }

        [JsonPropertyName("video_upload_url")]
        public string? VideoUploadUrl { get; set; }

        [JsonPropertyName("video_upload_token")]
        public string? VideoUploadToken { get; set; }

        [JsonPropertyName("video_path")]
        public string? VideoPath { get; set; }
    }

    /// <summary>
    /// Complete report creation after uploads
    /// </summary>
    public class ReportCompleteRequest
    {
        [JsonPropertyName("report_id")]
        public string ReportId { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("targeted")]
        public bool? Targeted { get; set; }

        [JsonPropertyName("desired_action")]
        public string? DesiredAction { get; set; }

        [JsonPropertyName("recording_start_utc")]
        public string? RecordingStartUtc { get; set; }

        [JsonPropertyName("flag_utc")]
        public string? FlagUtc { get; set; }

        [JsonPropertyName("clip_start_offset_sec")]
        public double? ClipStartOffsetSec { get; set; }

        [JsonPropertyName("clip_end_offset_sec")]
        public double? ClipEndOffsetSec { get; set; }

        [JsonPropertyName("audio_path")]
        public string? AudioPath { get; set; }

        [JsonPropertyName("microphone_path")]
        public string? MicrophonePath { get; set; }

        [JsonPropertyName("video_path")]
        public string? VideoPath { get; set; }
    }

    /// <summary>
    /// Response from creating a report
    /// </summary>
    public class CreateReportResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("report_id")]
        public string ReportId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Error response from API
    /// </summary>
    public class ApiErrorResponse
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;

        [JsonPropertyName("details")]
        public string? Details { get; set; }
    }
}

