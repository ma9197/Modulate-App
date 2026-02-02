using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinUI_App.Models;

namespace WinUI_App.Services
{
    /// <summary>
    /// Client for calling the Cloudflare Worker API
    /// </summary>
    public class ReportsApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _workerUrl;

        public ReportsApiClient()
        {
            _httpClient = new HttpClient();
            _workerUrl = AppConfig.Instance.WorkerUrl;
            // Large video uploads can easily exceed the default 100s timeout.
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        }

        /// <summary>
        /// Check if the API is healthy
        /// </summary>
        public async Task<bool> CheckHealthAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_workerUrl}/health");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Create a new toxicity report
        /// </summary>
        public async Task<(bool success, string reportId, string error)> CreateReportAsync(
            string accessToken, 
            CreateReportRequest reportRequest)
        {
            try
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_workerUrl}/reports");
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                httpRequest.Content = JsonContent.Create(reportRequest);

                var response = await _httpClient.SendAsync(httpRequest);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var error = JsonSerializer.Deserialize<ApiErrorResponse>(responseContent);
                    return (false, string.Empty, error?.Error ?? "Failed to create report");
                }

                var result = JsonSerializer.Deserialize<CreateReportResponse>(responseContent);
                if (result != null && result.Ok)
                {
                    return (true, result.ReportId, string.Empty);
                }

                return (false, string.Empty, "Invalid response from server");
            }
            catch (Exception ex)
            {
                return (false, string.Empty, $"Network error: {ex.Message}");
            }
        }

        /// <summary>
        /// Initialize report and request signed upload URLs
        /// </summary>
        public async Task<(bool success, ReportInitResponse? init, string error)> InitReportAsync(
            string accessToken,
            ReportInitRequest initRequest)
        {
            try
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_workerUrl}/reports/init");
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                httpRequest.Content = JsonContent.Create(initRequest);

                var response = await _httpClient.SendAsync(httpRequest);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var error = JsonSerializer.Deserialize<ApiErrorResponse>(responseContent);
                    return (false, null, error?.Error ?? "Failed to initialize report");
                }

                var result = JsonSerializer.Deserialize<ReportInitResponse>(responseContent);
                if (result != null && !string.IsNullOrEmpty(result.ReportId))
                {
                    return (true, result, string.Empty);
                }

                return (false, null, "Invalid response from server");
            }
            catch (Exception ex)
            {
                return (false, null, $"Network error: {ex.Message}");
            }
        }

        /// <summary>
        /// Upload a file to a signed URL
        /// </summary>
        public async Task<(bool success, string error)> UploadToSignedUrlAsync(
            string signedUrl,
            string filePath,
            string contentType,
            string? uploadToken = null,
            IProgress<double>? progress = null)
        {
            try
            {
                using var fileStream = File.OpenRead(filePath);
                var contentLength = fileStream.Length;

                HttpContent content = progress == null
                    ? new StreamContent(fileStream)
                    : new ProgressableStreamContent(fileStream, 64 * 1024, progress, contentLength);

                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                // Try PUT first (preferred for signed upload URLs)
                using var putRequest = new HttpRequestMessage(HttpMethod.Put, signedUrl)
                {
                    Content = content
                };

                if (!string.IsNullOrEmpty(uploadToken))
                {
                    putRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);
                }

                var response = await _httpClient.SendAsync(putRequest);
                if (!response.IsSuccessStatusCode)
                {
                    // Some signed URLs expect POST instead of PUT, retry once
                    fileStream.Position = 0;
                    HttpContent retryContent = progress == null
                        ? new StreamContent(fileStream)
                        : new ProgressableStreamContent(fileStream, 64 * 1024, progress, contentLength);
                    retryContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                    using var postRequest = new HttpRequestMessage(HttpMethod.Post, signedUrl)
                    {
                        Content = retryContent
                    };
                    if (!string.IsNullOrEmpty(uploadToken))
                    {
                        postRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);
                    }

                    response = await _httpClient.SendAsync(postRequest);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var responseText = await response.Content.ReadAsStringAsync();
                    return (false, $"Upload failed: {response.StatusCode} {responseText}");
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, $"Upload error: {ex.Message}");
            }
        }

        private sealed class ProgressableStreamContent : HttpContent
        {
            private readonly Stream _stream;
            private readonly int _bufferSize;
            private readonly IProgress<double> _progress;
            private readonly long _length;

            public ProgressableStreamContent(Stream stream, int bufferSize, IProgress<double> progress, long length)
            {
                _stream = stream;
                _bufferSize = bufferSize;
                _progress = progress;
                _length = length;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                var buffer = new byte[_bufferSize];
                long uploaded = 0;
                int read;
                while ((read = await _stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, read);
                    uploaded += read;
                    _progress.Report(_length == 0 ? 1.0 : (double)uploaded / _length);
                }
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, System.Threading.CancellationToken cancellationToken)
            {
                return SerializeToStreamAsync(stream, context);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _length;
                return true;
            }
        }

        /// <summary>
        /// Complete report creation after uploads
        /// </summary>
        public async Task<(bool success, string reportId, string error)> CompleteReportAsync(
            string accessToken,
            ReportCompleteRequest completeRequest)
        {
            try
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_workerUrl}/reports/complete");
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                httpRequest.Content = JsonContent.Create(completeRequest);

                var response = await _httpClient.SendAsync(httpRequest);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    var error = JsonSerializer.Deserialize<ApiErrorResponse>(responseContent);
                    return (false, string.Empty, error?.Error ?? "Failed to complete report");
                }

                var result = JsonSerializer.Deserialize<CreateReportResponse>(responseContent);
                if (result != null && result.Ok)
                {
                    return (true, result.ReportId, string.Empty);
                }

                return (false, string.Empty, "Invalid response from server");
            }
            catch (Exception ex)
            {
                return (false, string.Empty, $"Network error: {ex.Message}");
            }
        }
    }
}

