using System;
using System.Threading;

namespace EmbyCredits.Services
{
    public abstract class DebouncedPersistenceService : IDisposable
    {
        private Timer? _saveTimer;
        private const int SaveDebounceMs = 1000;
        protected readonly object _lock = new object();
        private volatile bool _disposed;

        protected void ScheduleSave()
        {
            if (_disposed) return;
            var newTimer = new Timer(_ => { if (!_disposed) FlushSave(); }, null, SaveDebounceMs, Timeout.Infinite);
            Interlocked.Exchange(ref _saveTimer, newTimer)?.Dispose();
        }

        protected abstract void FlushSave();

        public void Dispose()
        {
            _disposed = true;
            Interlocked.Exchange(ref _saveTimer, null)?.Dispose();
            FlushSave();
            GC.SuppressFinalize(this);
        }
    }
}
