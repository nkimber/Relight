using System.Text.Json.Serialization;

namespace Relight.Core;

public enum RecoveryState
{
    Disabled,
    WaitingForFirstStart,
    Starting,
    Observing,
    Healthy,
    RetryWaiting,
    AwaitingIntervention
}

public enum ObservationOrigin
{
    InitialAdoption,
    AutomaticLaunch,
    ExplicitStart,
    ExternalStart
}

public enum RecoveryHoldReason { InterruptedExplicitLaunch }

public enum DetectionKind { Present, Absent, Unavailable }

public enum DetectionDiagnosticCode
{
    MonitoringInterrupted, EnumerationFailed, CandidateInspectionFailed,
    ProcessDisappeared, OwnerUnavailable, CommandLineUnavailable,
    AmbiguousMainProcesses, ConfigurationUnavailable, ConfigurationChanged,
    ConfigurationReadFailed, AdapterException
}

public enum MonitoringInterruptionCause { Unspecified, PowerResume, SessionUnlock }

/// <summary>Safe diagnostic facts; never include command lines or exception messages.</summary>
public sealed record DetectionDiagnostic(DetectionDiagnosticCode Code,
    string? Adapter = null, string? Stage = null, int? CandidateProcessId = null,
    int? SessionId = null, int? CandidateCount = null, int? MainCount = null,
    string? ExceptionType = null, int? HResult = null,
    long? ActiveConfigurationRevision = null, long? DiskConfigurationRevision = null,
    MonitoringInterruptionCause? InterruptionCause = null);

public enum DetectionFailureKind
{
    Unknown,
    Ambiguous,
    PermissionDenied,
    InspectionFailed,
    ConfigurationChanged
}

/// <summary>
/// Identity is an opaque adapter-supplied logical instance key. An adapter must
/// incorporate current-session identity and a process start time, not just a PID.
/// </summary>
public sealed record Detection(DetectionKind Kind, string? Identity = null, string? Reason = null,
    DetectionFailureKind? FailureKind = null, int? NativeErrorCode = null,
    DetectionDiagnostic? Diagnostic = null)
{
    public static Detection Present(string identity) =>
        !string.IsNullOrWhiteSpace(identity)
            ? new(DetectionKind.Present, identity)
            : throw new ArgumentException("Target identity must be nonempty.", nameof(identity));

    public static Detection Absent() => new(DetectionKind.Absent);
    public static Detection Unavailable(string reason,
        DetectionFailureKind failureKind = DetectionFailureKind.Unknown,
        int? nativeErrorCode = null, DetectionDiagnostic? diagnostic = null) => new(DetectionKind.Unavailable,
            Reason: reason, FailureKind: failureKind, NativeErrorCode: nativeErrorCode,
            Diagnostic: diagnostic);
}

public enum RecoverySignal { None, LaunchDue }

public sealed record RecoveryTransition(
    RecoveryState Before,
    RecoveryState After,
    RecoverySignal Signal,
    string Reason);

public sealed record RecoverySnapshot(
    RecoveryState State,
    bool Enabled,
    bool Paused,
    bool DetectionUnavailable,
    bool Armed,
    bool LockedOut,
    int ReservedAutomaticAttempts,
    Guid? EpisodeId,
    Guid? OperationId,
    string? TargetIdentity,
    ObservationOrigin? ObservationOrigin,
    TimeSpan? ObservationStartedAt,
    TimeSpan? LastVerifiedAt,
    TimeSpan? AbsenceStartedAt,
    TimeSpan? RetryDeadline,
    TimeSpan? AppearanceDeadline,
    RecoveryHoldReason? HoldReason = null);

/// <summary>
/// Durable safety fields. Monotonic deadlines and process identity are intentionally
/// excluded: they cannot establish continuity across a supervisor restart.
/// </summary>
public sealed record RecoveryCheckpoint(
    bool Enabled,
    bool Paused,
    bool Armed,
    bool LockedOut,
    int ReservedAutomaticAttempts,
    Guid? EpisodeId,
    RecoveryState LastState,
    Guid? PendingOperationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? PendingExplicitStart = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RecoveryHoldReason? HoldReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? SharedBudgetRevision = null);
