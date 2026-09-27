using System.Runtime.InteropServices;
using Relight.Engine;

namespace Relight.Windows;

/// <summary>
/// Activates a registered package application by AUMID. The PID returned by
/// Windows is deliberately ignored: discovery must identify the lasting app.
/// </summary>
public sealed class PackagedApplicationLauncher : IProcessLauncher
{
    private static readonly Guid ActivationManagerClassId =
        new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private readonly string _applicationUserModelId;

    public PackagedApplicationLauncher(string applicationUserModelId)
    {
        int separator = applicationUserModelId?.IndexOf('!') ?? -1;
        if (string.IsNullOrWhiteSpace(applicationUserModelId) ||
            applicationUserModelId.Length > 300 ||
            applicationUserModelId.Count(c => c == '!') != 1 ||
            separator <= 0 || separator == applicationUserModelId.Length - 1 ||
            applicationUserModelId.Any(c => !(char.IsLetterOrDigit(c) ||
                c is '.' or '-' or '_' or '!')))
            throw new ArgumentException("A valid application user model ID is required.",
                nameof(applicationUserModelId));
        _applicationUserModelId = applicationUserModelId;
    }

    public Task LaunchAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.Run(() => Activate(cancellationToken), cancellationToken);

    private void Activate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Type managerType = Type.GetTypeFromCLSID(ActivationManagerClassId,
            throwOnError: true)!;
        object managerObject = Activator.CreateInstance(managerType) ??
            throw new InvalidOperationException("Windows application activation is unavailable.");
        try
        {
            var manager = (IApplicationActivationManager)managerObject;
            cancellationToken.ThrowIfCancellationRequested();
            int result = manager.ActivateApplication(_applicationUserModelId,
                null, 0, out _);
            Marshal.ThrowExceptionForHR(result);
        }
        finally { Marshal.ReleaseComObject(managerObject); }
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            int options, out uint processId);
    }
}
