using MediaBrowser.Controller.Entities.TV;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EmbyCredits.Services
{
    public class EpisodeQueue : IDisposable
    {
        private readonly Channel<Episode> _channel;
        private bool _disposed;

        public EpisodeQueue(int capacity = 1000)
        {
            _channel = Channel.CreateBounded<Episode>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public int Count => _channel.Reader.Count;

        public async ValueTask EnqueueAsync(Episode episode, CancellationToken cancellationToken = default)
        {
            await _channel.Writer.WriteAsync(episode, cancellationToken).ConfigureAwait(false);
        }

        public bool TryEnqueue(Episode episode)
        {
            return _channel.Writer.TryWrite(episode);
        }

        public async ValueTask<Episode> DequeueAsync(CancellationToken cancellationToken = default)
        {
            return await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        public bool TryDequeue(out Episode? episode)
        {
            return _channel.Reader.TryRead(out episode);
        }

        public async IAsyncEnumerable<Episode> DequeueAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var episode in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return episode;
            }
        }

        public void CompleteAdding()
        {
            _channel.Writer.Complete();
        }

        public void Clear()
        {
            while (_channel.Reader.TryRead(out _))
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _channel.Writer.Complete();
        }
    }
}
