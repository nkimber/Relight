using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Relight.TestTarget;

internal sealed record TargetOptions(
    string Label,
    bool Helper,
    bool Handoff,
    bool SpawnHelper,
    bool Hidden,
    bool BlockClose,
    int? ExitAfterMs,
    int HelperMs,
    string? ReadyFile,
    string? AttemptFile,
    int FailFirstCount)
{
    public static TargetOptions Parse(string[] args)
    {
        string label = "test";
        bool helper = false, handoff = false, spawnHelper = false, hidden = false,
            blockClose = false;
        int? exitAfterMs = null;
        int helperMs = 30_000;
        string? readyFile = null, attemptFile = null;
        int failFirstCount = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"Missing value for {args[i]}.");
            switch (args[i])
            {
                case "--label": label = Next(); break;
                case "--helper": helper = true; break;
                case "--handoff": handoff = true; break;
                case "--spawn-helper": spawnHelper = true; break;
                case "--hidden": hidden = true; break;
                case "--block-close": blockClose = true; break;
                case "--exit-after-ms": exitAfterMs = ParseDuration(Next()); break;
                case "--helper-ms": helperMs = ParseDuration(Next()); break;
                case "--ready-file": readyFile = Next(); break;
                case "--attempt-file": attemptFile = Next(); break;
                case "--fail-first":
                    if (!int.TryParse(Next(), out failFirstCount) ||
                        failFirstCount is < 0 or > 20)
                        throw new ArgumentException("Fail-first count must be 0–20.");
                    break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(label) || label.Length > 60)
            throw new ArgumentException("Label must be 1–60 characters.");
        if (helper && (handoff || spawnHelper))
            throw new ArgumentException("A helper cannot hand off or spawn another helper.");
        if (attemptFile is not null && !Path.IsPathFullyQualified(attemptFile))
            throw new ArgumentException("Attempt-file path must be absolute.");
        if (failFirstCount > 0 && (attemptFile is null || exitAfterMs is null))
            throw new ArgumentException("Fail-first mode requires an attempt file and exit delay.");
        return new(label, helper, handoff, spawnHelper, hidden, blockClose,
            exitAfterMs, helperMs, readyFile, attemptFile, failFirstCount);
    }

    private static int ParseDuration(string text) =>
        int.TryParse(text, out int value) && value is >= 1 and <= 3_600_000
            ? value
            : throw new ArgumentException("Duration must be 1–3,600,000 milliseconds.");
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        TargetOptions options;
        try { options = TargetOptions.Parse(args); }
        catch (ArgumentException) { return 2; }

        if (options.Handoff)
        {
            // Simulates a short-lived activator: its exit says nothing about the real target.
            StartChild(options, helper: false);
            return 0;
        }

        if (options.SpawnHelper)
            StartChild(options, helper: true);

        if (!options.Helper && options.AttemptFile is { } attemptFile &&
            RecordAttempt(attemptFile) > options.FailFirstCount)
            options = options with { ExitAfterMs = null };

        ApplicationConfiguration.Initialize();
        Application.Run(new TargetForm(options));
        return 0;
    }

    private static int RecordAttempt(string path)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        int previous = 0;
        if (stream.Length == bytes.Length)
        {
            stream.ReadExactly(bytes);
            previous = BitConverter.ToInt32(bytes);
        }
        else if (stream.Length != 0)
            throw new InvalidDataException("Attempt counter has an invalid length.");
        if (previous is < 0 or >= 20)
            throw new InvalidDataException("Attempt counter is out of range.");
        int next = previous + 1;
        BitConverter.TryWriteBytes(bytes, next);
        stream.Position = 0;
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
        stream.Flush(flushToDisk: true);
        return next;
    }

    private static void StartChild(TargetOptions options, bool helper)
    {
        string executable = Environment.ProcessPath ??
            throw new InvalidOperationException("Test target executable path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--label");
        start.ArgumentList.Add(options.Label);
        if (options.Hidden) start.ArgumentList.Add("--hidden");
        if (helper)
        {
            start.ArgumentList.Add("--helper");
            start.ArgumentList.Add("--exit-after-ms");
            start.ArgumentList.Add(options.HelperMs.ToString());
            if (options.ReadyFile is { } path)
            {
                start.ArgumentList.Add("--ready-file");
                start.ArgumentList.Add(path + ".helper");
            }
        }
        else
        {
            if (options.SpawnHelper) start.ArgumentList.Add("--spawn-helper");
            start.ArgumentList.Add("--helper-ms");
            start.ArgumentList.Add(options.HelperMs.ToString());
            if (options.BlockClose) start.ArgumentList.Add("--block-close");
            if (options.ExitAfterMs is { } duration)
            {
                start.ArgumentList.Add("--exit-after-ms");
                start.ArgumentList.Add(duration.ToString());
            }
            if (options.ReadyFile is { } path)
            {
                start.ArgumentList.Add("--ready-file");
                start.ArgumentList.Add(path);
            }
            if (options.AttemptFile is { } attemptFile)
            {
                start.ArgumentList.Add("--attempt-file");
                start.ArgumentList.Add(attemptFile);
                start.ArgumentList.Add("--fail-first");
                start.ArgumentList.Add(options.FailFirstCount.ToString());
            }
        }

        using Process launched = Process.Start(start) ??
            throw new InvalidOperationException("Test target child launch failed.");
    }
}

internal sealed class TargetForm : Form
{
    private readonly System.Windows.Forms.Timer? _exitTimer;
    private bool _timedExit;

    public TargetForm(TargetOptions options)
    {
        Text = options.Helper ? $"Relight helper: {options.Label}" : $"Relight target: {options.Label}";
        ShowInTaskbar = !options.Helper && !options.Hidden;
        Width = options.Helper || options.Hidden ? 1 : 460;
        Height = options.Helper || options.Hidden ? 1 : 180;
        if (options.Helper || options.Hidden) Opacity = 0;

        var label = new Label
        {
            Text = options.Helper
                ? "Helper process"
                : $"Controllable test target · {options.Label}\nPID {Environment.ProcessId}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(label);

        if (options.ExitAfterMs is { } duration)
        {
            _exitTimer = new System.Windows.Forms.Timer { Interval = duration };
            _exitTimer.Tick += (_, _) =>
            {
                _timedExit = true;
                _exitTimer.Stop();
                Close();
            };
            Shown += (_, _) => _exitTimer.Start();
        }

        Shown += (_, _) =>
        {
            if (options.ReadyFile is { } path)
            {
                using Process current = Process.GetCurrentProcess();
                File.WriteAllText(path,
                    $"{Environment.ProcessId}|{current.StartTime.ToUniversalTime():O}");
            }
        };
        FormClosing += (_, eventArgs) =>
        {
            if (options.BlockClose && !_timedExit)
                eventArgs.Cancel = true;
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _exitTimer?.Dispose();
        base.Dispose(disposing);
    }
}
