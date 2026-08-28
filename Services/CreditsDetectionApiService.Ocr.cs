using System;
using System.Threading.Tasks;
using EmbyCredits.Api;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public partial class CreditsDetectionApiService
    {
        // Status values: "Ready", "Unreachable" (host/port not answering - may still be starting), "BadResponse" (reached but unexpected data), "NotConfigured", "Error"
        private const int OcrTestMaxAttempts = 3;
        private static readonly TimeSpan OcrTestRetryDelay = TimeSpan.FromSeconds(3);

        public async Task<object> Post(TestOcrConnectionRequest request)
        {
            try
            {
                if (request.OcrEngine == "LocalTesseract")
                {
                    var configuredPath = Plugin.Instance?.Configuration?.LocalTesseractPath ?? string.Empty;
                    var (available, message) = LocalTesseractService.TestAvailability(configuredPath);
                    return new { Success = available, Status = available ? "Ready" : "Unreachable", Message = message };
                }

                if (string.IsNullOrWhiteSpace(request.OcrEndpoint))
                {
                    return new { Success = false, Status = "NotConfigured", Message = "OCR endpoint URL is required" };
                }

                if (!IsValidOcrEndpointUrl(request.OcrEndpoint))
                {
                    return new { Success = false, Status = "NotConfigured", Message = "Invalid OCR endpoint URL. Only localhost and local network addresses are allowed." };
                }

                var endpoint = request.OcrEndpoint.TrimEnd('/');
                string lastMessage = "Cannot reach OCR server";

                for (var attempt = 1; attempt <= OcrTestMaxAttempts; attempt++)
                {
                    var (success, status, message) = await TryOcrConnectionOnce(request.OcrEngine, endpoint).ConfigureAwait(false);

                    if (success)
                    {
                        var retryNote = attempt > 1 ? $" (container may have still been starting up - succeeded on attempt {attempt}/{OcrTestMaxAttempts})" : "";
                        return new { Success = true, Status = "Ready", Message = message + retryNote };
                    }

                    lastMessage = message;

                    // Only retry when the server can't be reached at all - a bad response means it's up but misbehaving
                    if (status != "Unreachable" || attempt == OcrTestMaxAttempts)
                    {
                        var finalMessage = status == "Unreachable"
                            ? $"Cannot reach OCR server after {attempt} attempt(s) - the container may still be starting, not running, or the port is wrong. Last error: {message}"
                            : message;
                        return new { Success = false, Status = status, Message = finalMessage };
                    }

                    await Task.Delay(OcrTestRetryDelay).ConfigureAwait(false);
                }

                return new { Success = false, Status = "Unreachable", Message = lastMessage };
            }
            catch (TaskCanceledException)
            {
                return new { Success = false, Status = "Unreachable", Message = "Connection timed out (15 seconds)" };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error testing OCR connection", ex);
                return new { Success = false, Status = "Error", Message = $"Error: {ex.Message}" };
            }
        }

        private async Task<(bool Success, string Status, string Message)> TryOcrConnectionOnce(string ocrEngine, string endpoint)
        {
            using (var httpClient = new System.Net.Http.HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromSeconds(15);

                if (ocrEngine != "PaddleOCR")
                {
                    try
                    {
                        using (var pingResponse = await httpClient.GetAsync(endpoint).ConfigureAwait(false))
                        {
                            if (!pingResponse.IsSuccessStatusCode)
                            {
                                return (false, "BadResponse", $"OCR server returned status: {pingResponse.StatusCode}");
                            }
                        }
                    }
                    catch (System.Net.Http.HttpRequestException ex)
                    {
                        return (false, "Unreachable", ex.Message);
                    }
                    catch (TaskCanceledException)
                    {
                        return (false, "Unreachable", "Request timed out");
                    }
                }

                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    var resourceName = "EmbyCredits.Images.logo.jpg";

                    byte[] imageBytes;
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream == null)
                        {
                            return (false, "Error", "Logo not found in embedded resources.");
                        }

                        using (var memoryStream = new System.IO.MemoryStream())
                        {
                            stream.CopyTo(memoryStream);
                            imageBytes = memoryStream.ToArray();
                        }
                    }

                    using (var content = new System.Net.Http.MultipartFormDataContent())
                    using (var imageContent = new System.Net.Http.ByteArrayContent(imageBytes))
                    {
                        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

                        string ocrEndpoint;
                        if (ocrEngine == "PaddleOCR")
                        {
                            content.Add(imageContent, "file", "logo.jpg");
                            ocrEndpoint = endpoint + "/ocr/file";
                        }
                        else
                        {
                            content.Add(imageContent, "file", "logo.jpg");
                            var optionsContent = new System.Net.Http.StringContent("{\"languages\":[\"eng\"]}");
                            content.Add(optionsContent, "options");
                            ocrEndpoint = endpoint + "/tesseract";
                        }

                        System.Net.Http.HttpResponseMessage ocrResponse;
                        try
                        {
                            ocrResponse = await httpClient.PostAsync(ocrEndpoint, content).ConfigureAwait(false);
                        }
                        catch (System.Net.Http.HttpRequestException ex)
                        {
                            return (false, "Unreachable", ex.Message);
                        }
                        catch (TaskCanceledException)
                        {
                            return (false, "Unreachable", "Request timed out");
                        }

                        using (ocrResponse)
                        {
                            if (!ocrResponse.IsSuccessStatusCode)
                            {
                                var errorContent = await ocrResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                                return (false, "BadResponse", $"OCR processing failed with status: {ocrResponse.StatusCode}. Details: {errorContent.Substring(0, Math.Min(200, errorContent.Length))}");
                            }

                            var ocrResult = await ocrResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var engineLabel = ocrEngine == "PaddleOCR" ? "PaddleOCR" : "Tesseract";

                            if (ocrResult.Contains("\"data\"") && ocrResult.Contains("\"stdout\"") && ocrResult.Length > 20)
                            {
                                return (true, "Ready", $"✓ Connection successful! {engineLabel} server is responding correctly.");
                            }

                            return (false, "BadResponse", $"{engineLabel} server responded but returned unexpected format: {ocrResult.Substring(0, Math.Min(150, ocrResult.Length))}...");
                        }
                    }
                }
                catch (Exception ocrEx)
                {
                    return (false, "Error", $"OCR test failed: {ocrEx.Message}");
                }
            }
        }

        public async Task<object> Post(GetOcrEngineStatusRequest request)
        {
            try
            {
                if (request.OcrEngine == "LocalTesseract")
                {
                    var configuredPath = Plugin.Instance?.Configuration?.LocalTesseractPath ?? string.Empty;
                    var (available, message) = LocalTesseractService.TestAvailability(configuredPath);
                    return new { Success = available, Status = available ? "Ready" : "Unreachable", Message = message };
                }

                if (string.IsNullOrWhiteSpace(request.OcrEndpoint) || !IsValidOcrEndpointUrl(request.OcrEndpoint))
                {
                    return new { Success = false, Status = "NotConfigured", Message = "OCR endpoint is not configured" };
                }

                var endpoint = request.OcrEndpoint.TrimEnd('/');

                // Different container images expose different lightweight health paths (PaddleOCR uses /health, Tesseract uses /status) - try the expected one first, then fall back
                var healthPaths = request.OcrEngine == "PaddleOCR"
                    ? new[] { "/health", "/status" }
                    : new[] { "/status", "/health" };

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(5);

                    foreach (var path in healthPaths)
                    {
                        try
                        {
                            using (var response = await httpClient.GetAsync(endpoint + path).ConfigureAwait(false))
                            {
                                if (response.IsSuccessStatusCode)
                                {
                                    return new { Success = true, Status = "Ready", Message = "OCR engine is ready" };
                                }

                                if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                                {
                                    return new { Success = false, Status = "BadResponse", Message = $"OCR server returned status: {response.StatusCode}" };
                                }
                                // 404 on this path - try the next candidate path below
                            }
                        }
                        catch (System.Net.Http.HttpRequestException)
                        {
                            return new { Success = false, Status = "Unreachable", Message = "Not reachable yet - the container may still be starting or not running" };
                        }
                        catch (TaskCanceledException)
                        {
                            return new { Success = false, Status = "Unreachable", Message = "OCR server did not respond in time" };
                        }
                    }

                    return new { Success = false, Status = "BadResponse", Message = "OCR server is reachable but has no /health or /status endpoint" };
                }
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error checking OCR engine status", ex);
                return new { Success = false, Status = "Error", Message = "Error checking OCR engine status" };
            }
        }

        private bool IsValidOcrEndpointUrl(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            try
            {
                var uri = new Uri(endpoint);
                
                if (uri.Scheme != "http" && uri.Scheme != "https")
                    return false;

                var host = uri.Host.ToLowerInvariant();
                if (host == "localhost" || host == "127.0.0.1" || host == "::1")
                    return true;

                if (System.Net.IPAddress.TryParse(host, out var ipAddress))
                {
                    var bytes = ipAddress.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        if (bytes[0] == 10) return true;
                        if (bytes[0] == 192 && bytes[1] == 168) return true;
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                    }
                }

                _logger?.Warn($"OCR endpoint is a public/non-local address: {endpoint}. Ensure this is intentional.");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error($"Invalid OCR endpoint URI: {endpoint} - {ex.Message}");
                return false;
            }
        }

        private string SanitizeErrorMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "An error occurred";

            var sanitized = System.Text.RegularExpressions.Regex.Replace(
                message,
                @"[A-Za-z]:\\\S+|/(?:[a-zA-Z0-9_./\-])+",
                "[path]");
            
            return sanitized;
        }
    }
}
