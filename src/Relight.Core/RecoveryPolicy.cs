namespace Relight.Core;

public sealed record RecoveryPolicy(
    TimeSpan NormalPollInterval,
    TimeSpan ObservationPeriod,
    TimeSpan ObservationPollInterval,
    TimeSpan LockoutDiscoveryInterval,
    int MaximumAutomaticAttempts,
    TimeSpan RetryDelay,
    TimeSpan AppearanceTimeout,
    TimeSpan AbsenceConfirmationDelay,
    bool StartAutomaticallyWhenInitiallyAbsent,
    bool RearmAfterStableExternalStart)
{
    public static RecoveryPolicy Default { get; } = new(
        TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30), 3, TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2), false, true);

    public void Validate()
    {
        CheckRange(NormalPollInterval, 60, 300, nameof(NormalPollInterval));
        CheckRange(ObservationPeriod, 60, 3600, nameof(ObservationPeriod));
        CheckRange(ObservationPollInterval, 1, 30, nameof(ObservationPollInterval));
        CheckRange(LockoutDiscoveryInterval, 5, 300, nameof(LockoutDiscoveryInterval));
        CheckRange(RetryDelay, 5, 3600, nameof(RetryDelay));
        CheckRange(AppearanceTimeout, 5, 300, nameof(AppearanceTimeout));
        CheckRange(AbsenceConfirmationDelay, 1, 10, nameof(AbsenceConfirmationDelay));
        if (ObservationPollInterval > NormalPollInterval)
            throw new ArgumentOutOfRangeException(nameof(ObservationPollInterval));
        if (MaximumAutomaticAttempts is < 0 or > 20)
            throw new ArgumentOutOfRangeException(nameof(MaximumAutomaticAttempts));
    }

    private static void CheckRange(TimeSpan value, int minimumSeconds, int maximumSeconds, string name)
    {
        if (value < TimeSpan.FromSeconds(minimumSeconds) ||
            value > TimeSpan.FromSeconds(maximumSeconds))
            throw new ArgumentOutOfRangeException(name);
    }
}
