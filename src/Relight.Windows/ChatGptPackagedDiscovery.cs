using System.Diagnostics;
using Relight.Core;
using Relight.Engine;

namespace Relight.Windows;

/// <summary>
/// Read-only discovery for the selected ChatGPT package. The observed
/// Electron --type switch identifies helpers; a lone process without that
/// switch is the main candidate. This is not a generic packaged-app rule.
/// </summary>
public sealed class ChatGptPackagedDiscovery : IProcessDiscovery
{
    public const string PackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";
    public const string ApplicationUserModelId = PackageFamilyName + "!App";
    private readonly PackagedProcessProbe _probe = new(PackageFamilyName, "ChatGPT.exe");
    private readonly int _sessionId;

    public ChatGptPackagedDiscovery()
    {
        using Process current = Process.GetCurrentProcess();
        _sessionId = current.SessionId;
    }

    public async Task<Detection> DetectAsync(CancellationToken cancellationToken)
    {
        PackagedProcessInspection inspection = await _probe.InspectAsync(cancellationToken)
            .ConfigureAwait(false);
        return Interpret(inspection, _sessionId);
    }

    public static Detection Interpret(PackagedProcessInspection inspection, int sessionId)
    {
        if (inspection.Problem is { } problem)
            return Detection.Unavailable(problem,
                inspection.FailureKind ?? DetectionFailureKind.Unknown,
                inspection.NativeErrorCode);
        PackagedProcessCandidate[] main = inspection.Candidates
            .Where(candidate => !candidate.HasTypeSwitch).ToArray();
        return main.Length switch
        {
            0 => Detection.Absent(),
            1 => Detection.Present(
                $"{sessionId}|{PackageFamilyName}|{main[0].ProcessId}|{main[0].StartedUtcTicks}"),
            _ => Detection.Unavailable(
                $"{main.Length} ChatGPT main-process candidates; identity is ambiguous.",
                DetectionFailureKind.Ambiguous)
        };
    }
}
