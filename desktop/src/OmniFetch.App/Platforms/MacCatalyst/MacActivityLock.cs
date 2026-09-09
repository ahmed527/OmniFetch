using System;
using Foundation;

namespace OmniFetch.App.Platforms.MacCatalyst;

/// <summary>
/// Service interface for acquiring and releasing system activity locks to prevent macOS App Nap and sleep.
/// </summary>
public interface IActivityLockService
{
    IDisposable AcquireTransferLock(string reason);
}

/// <summary>
/// Mac Catalyst implementation of system sleep and App Nap prevention using NSProcessInfo.
/// </summary>
public sealed class MacActivityLockService : IActivityLockService
{
    public IDisposable AcquireTransferLock(string reason)
    {
        return new MacActivityLock(reason);
    }

    private sealed class MacActivityLock : IDisposable
    {
        private NSObject? _activityToken;
        private bool _disposed;

        public MacActivityLock(string reason)
        {
            try
            {
                // Prevent system sleep and CPU/network throttling during active downloads
                const NSActivityOptions options = NSActivityOptions.UserInitiated 
                                                | NSActivityOptions.LatencyCritical 
                                                | NSActivityOptions.IdleSystemSleepDisabled;

                _activityToken = NSProcessInfo.ProcessInfo.BeginActivity(options, reason);
            }
            catch
            {
                _activityToken = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_activityToken != null)
            {
                try
                {
                    NSProcessInfo.ProcessInfo.EndActivity(_activityToken);
                }
                catch
                {
                    // Ignored on teardown
                }
                _activityToken = null;
            }
        }
    }
}
