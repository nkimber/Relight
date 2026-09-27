using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace Relight.Windows;

public interface IStartupRegistryValue
{
    string? Read();
    void Write(string? command);
}

public sealed class CurrentUserRunRegistryValue : IStartupRegistryValue
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Relight";

    public string? Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunPath, writable: false);
        object? value = key?.GetValue(ValueName, null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            null => null,
            string command => command,
            _ => throw new InvalidOperationException(
                "The current-user Relight startup value is not a string.")
        };
    }

    public void Write(string? command)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunPath, writable: true)
            ?? throw new IOException("The current-user startup key could not be opened.");
        if (command is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}

public sealed record StartupRegistrationStatus(
    bool EnabledForThisExecutable,
    bool RegisteredToAnotherRelightExecutable,
    bool ConflictingValue);

public sealed class CurrentUserStartupRegistration
{
    private static readonly Regex RelightCommand = new(
        "^\"[^\"]+[\\\\/]Relight\\.exe\" --tray$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private readonly IStartupRegistryValue _registry;
    private readonly string _command;

    public CurrentUserStartupRegistration(string executablePath,
        IStartupRegistryValue? registry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullPath = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetFileName(fullPath), "Relight.exe",
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sign-in startup requires Relight.exe.",
                nameof(executablePath));
        _command = $"\"{fullPath}\" --tray";
        if (_command.Length > 260)
            throw new ArgumentException(
                "The executable path is too long for Windows sign-in startup.",
                nameof(executablePath));
        _registry = registry ?? new CurrentUserRunRegistryValue();
    }

    public StartupRegistrationStatus Inspect()
    {
        string? current = _registry.Read();
        return new(
            EnabledForThisExecutable: string.Equals(current, _command,
                StringComparison.OrdinalIgnoreCase),
            RegisteredToAnotherRelightExecutable: current is not null &&
                !string.Equals(current, _command, StringComparison.OrdinalIgnoreCase) &&
                RelightCommand.IsMatch(current),
            ConflictingValue: current is not null && !RelightCommand.IsMatch(current));
    }

    public StartupRegistrationChange Apply(bool enabled)
    {
        string? before = _registry.Read();
        if (before is not null && !RelightCommand.IsMatch(before))
            throw new InvalidOperationException(
                "A different current-user startup command already uses the Relight name.");
        string? after = enabled ? _command : null;
        if (!string.Equals(before, after, StringComparison.Ordinal))
            _registry.Write(after);
        return new StartupRegistrationChange(_registry, before, after);
    }
}

public sealed class StartupRegistrationChange(
    IStartupRegistryValue registry, string? before, string? after)
{
    public void Rollback()
    {
        string? current = registry.Read();
        if (!string.Equals(current, after, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The sign-in startup entry changed again; automatic rollback was not attempted.");
        if (!string.Equals(before, after, StringComparison.Ordinal))
            registry.Write(before);
    }
}
