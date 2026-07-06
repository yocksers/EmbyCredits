using System;
using System.Threading;

namespace EmbyCredits.Services
{
    public abstract class DebouncedPersistenceService : IDisposable
    {
        private Timer? _saveTimer;
        private const int SaveDebounceMs = 1000;
        protected readonly object _lock = new object();

        protected void ScheduleSave()
        {
            var newTimer = new Timer(_ => FlushSave(), null, SaveDebounceMs, Timeout.Infinite);
            Interlocked.Exchange(ref _saveTimer, newTimer)?.Dispose();
        }

        protected abstract void FlushSave();

        public void Dispose()
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            FlushSave();
            GC.SuppressFinalize(this);
        }
    }
}
