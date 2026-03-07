using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyCredits.Services.Http
{
    public interface IHttpClientWrapper
    {
        Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken cancellationToken = default);
        Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken = default);
    }

    public class HttpClientWrapper : IHttpClientWrapper, IDisposable
    {
        private readonly HttpClient _httpClient;
        private bool _disposed;

        public HttpClientWrapper(TimeSpan timeout)
        {
            _httpClient = new HttpClient
            {
                Timeout = timeout
            };
            _httpClient.DefaultRequestHeaders.ConnectionClose = false;
        }

        public async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken cancellationToken = default)
        {
            return await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        }

        public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken = default)
        {
            return await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _httpClient?.Dispose();
        }
    }

    public class HttpClientPool : IDisposable
    {
        private static readonly Lazy<HttpClientPool> _instance = new Lazy<HttpClientPool>(() => new HttpClientPool());
        private readonly HttpClientWrapper _defaultClient;
        private bool _disposed;

        public static HttpClientPool Instance => _instance.Value;

        private HttpClientPool()
        {
            _defaultClient = new HttpClientWrapper(TimeSpan.FromMinutes(2));
        }

        public IHttpClientWrapper GetClient()
        {
            return _defaultClient;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _defaultClient?.Dispose();
        }
    }
}
