using System;
using System.Security.Principal;
using System.Threading;

namespace Relight.Services;

/// <summary>One shell per user interactive session, with a latched activation signal.</summary>
internal sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private RegisteredWaitHandle? _registration;

    public SingleInstanceService()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userId = identity.User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        // Local kernel objects are session-scoped. The SID separates users in that session.
        string prefix = $@"Local\Relight.{userId}";
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Activate");
        try
        {
            _mutex = new Mutex(false, prefix + ".Instance");
            try
            {
                IsPrimary = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                IsPrimary = true;
            }
        }
        catch
        {
            _activation.Dispose();
            throw;
        }
    }

    public bool IsPrimary { get; }

    public void ActivatePrimary() => _activation.Set();

    public void Listen(Action activate)
    {
        if (!IsPrimary || _registration is not null)
        {
            throw new InvalidOperationException("Only the primary instance can register activation once.");
        }

        _registration = ThreadPool.RegisterWaitForSingleObject(_activation,
            (_, _) => activate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activation.Dispose();
        // Construct and dispose on the WPF dispatcher: mutex ownership is thread-specific.
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
