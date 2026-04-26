using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyCredits.Services
{
    public class PluginCoordinationService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly PluginConfiguration _configuration;
        private DateTime _lastIntroSkipperCheck = DateTime.MinValue;
        private bool _introSkipperInstalled = false;
        private WeakReference? _introSkipperWeakRef = null;
        private PropertyInfo? _introSkipperProcessingProperty = null;
        private const int CheckCacheSeconds = 30;
        private bool _disposed = false;

        public PluginCoordinationService(ILogger logger, PluginConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public bool IsOtherPluginProcessing()
        {
            if (!_configuration.PreventConcurrentPluginProcessing)
            {
                return false;
            }

            return IsIntroSkipperProcessing();
        }

        public async Task WaitForOtherPlugins(CancellationToken cancellationToken = default)
        {
            if (!_configuration.PreventConcurrentPluginProcessing)
            {
                return;
            }

            var waitCount = 0;
            while (IsOtherPluginProcessing() && !cancellationToken.IsCancellationRequested)
            {
                if (waitCount == 0)
                {
                    _logger.Info("Intro Skipper is processing - pausing EmbyCredits detection");
                }

                waitCount++;
                
                if (waitCount % 6 == 0)
                {
                    _logger.Info($"Still waiting for Intro Skipper to complete... ({waitCount * 5} seconds)");
                }

                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
            }

            if (waitCount > 0 && !cancellationToken.IsCancellationRequested)
            {
                _logger.Info($"Intro Skipper processing complete - resuming EmbyCredits detection (waited {waitCount * 5} seconds)");
            }
        }

        private bool IsIntroSkipperProcessing()
        {
            try
            {
                if (!_introSkipperInstalled && (DateTime.UtcNow - _lastIntroSkipperCheck).TotalSeconds > CheckCacheSeconds)
                {
                    DiscoverIntroSkipper();
                    _lastIntroSkipperCheck = DateTime.UtcNow;
                }

                if (!_introSkipperInstalled || _introSkipperWeakRef == null)
                {
                    return false;
                }

                var introSkipperInstance = _introSkipperWeakRef.Target;
                if (introSkipperInstance == null)
                {
                    _introSkipperInstalled = false;
                    _introSkipperWeakRef = null;
                    _introSkipperProcessingProperty = null;
                    return false;
                }

                if (_introSkipperProcessingProperty != null && _introSkipperProcessingProperty.CanRead)
                {
                    var value = _introSkipperProcessingProperty.GetValue(introSkipperInstance);
                    if (value is bool isProcessing)
                    {
                        return isProcessing;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.Debug($"Error checking Intro Skipper status: {ex.Message}");
                return false;
            }
        }

        private void DiscoverIntroSkipper()
        {
            try
            {
                if (Plugin.Instance == null)
                {
                    return;
                }

                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                
                var introSkipperAssembly = assemblies.FirstOrDefault(a => 
                    a.GetName().Name?.Contains("IntroSkipper", StringComparison.OrdinalIgnoreCase) == true ||
                    a.GetName().Name?.Contains("Intro.Skipper", StringComparison.OrdinalIgnoreCase) == true);

                if (introSkipperAssembly == null)
                {
                    _introSkipperInstalled = false;
                    _introSkipperWeakRef = null;
                    _introSkipperProcessingProperty = null;
                    return;
                }

                _logger.Debug($"Found Intro Skipper assembly: {introSkipperAssembly.FullName}");
                _introSkipperInstalled = true;

                var pluginType = introSkipperAssembly.GetTypes()
                    .FirstOrDefault(t => t.Name.Contains("Plugin", StringComparison.OrdinalIgnoreCase));

                if (pluginType != null)
                {
                    var instanceProperty = pluginType.GetProperty("Instance", 
                        BindingFlags.Public | BindingFlags.Static);
                    
                    if (instanceProperty != null && instanceProperty.CanRead)
                    {
                        var introSkipperInstance = instanceProperty.GetValue(null);
                        
                        if (introSkipperInstance != null)
                        {
                            _introSkipperWeakRef = new WeakReference(introSkipperInstance);
                            
                            var instanceType = introSkipperInstance.GetType();
                            
                            string[] processingPropertyNames = { 
                                "IsProcessing", 
                                "IsRunning", 
                                "IsAnalyzing", 
                                "IsScanning",
                                "CurrentlyProcessing"
                            };

                            foreach (var propName in processingPropertyNames)
                            {
                                var prop = instanceType.GetProperty(propName, 
                                    BindingFlags.Public | BindingFlags.Instance);
                                
                                if (prop != null && prop.PropertyType == typeof(bool) && prop.CanRead)
                                {
                                    _introSkipperProcessingProperty = prop;
                                    _logger.Info($"Intro Skipper coordination enabled - monitoring '{propName}' property");
                                    break;
                                }
                            }

                            if (_introSkipperProcessingProperty == null)
                            {
                                _logger.Debug("Intro Skipper found but no processing status property detected");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Error discovering Intro Skipper: {ex.Message}");
                _introSkipperInstalled = false;
                _introSkipperWeakRef = null;
                _introSkipperProcessingProperty = null;
            }
        }

        public string GetCoordinationStatus()
        {
            if (!_configuration.PreventConcurrentPluginProcessing)
            {
                return "Coordination disabled";
            }

            if (_introSkipperInstalled)
            {
                var isProcessing = IsIntroSkipperProcessing();
                return isProcessing 
                    ? "Intro Skipper is processing - EmbyCredits paused" 
                    : "Intro Skipper detected - coordination active";
            }

            return "No other plugins detected - running normally";
        }

        public bool IsIntroSkipperInstalled()
        {
            if ((DateTime.UtcNow - _lastIntroSkipperCheck).TotalSeconds > CheckCacheSeconds)
            {
                DiscoverIntroSkipper();
                _lastIntroSkipperCheck = DateTime.UtcNow;
            }

            return _introSkipperInstalled;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _introSkipperWeakRef = null;
                _introSkipperProcessingProperty = null;
            }

            _disposed = true;
        }
    }
}
