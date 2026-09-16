using System.Globalization;

namespace Batterio.StartupProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var stateFile = GetRequiredValue(args, "--state-file");
            var failuresBeforeReady = GetIntegerValue(args, "--fail-before-ready", defaultValue: 0);
            var exitAfterReadyMilliseconds = GetOptionalIntegerValue(args, "--ready-then-exit-ms");
            if (failuresBeforeReady < 0 || exitAfterReadyMilliseconds is < 0)
            {
                return 64;
            }

            var attempt = await IncrementAttemptAsync(stateFile).ConfigureAwait(false);
            if (attempt <= failuresBeforeReady)
            {
                return 23;
            }

            var eventName = Environment.GetEnvironmentVariable("BATTERIO_STARTUP_READY_EVENT");
            Environment.SetEnvironmentVariable("BATTERIO_STARTUP_READY_EVENT", null);
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return 65;
            }

            EventWaitHandle readyEvent;
            try
            {
                readyEvent = EventWaitHandle.OpenExisting(eventName);
            }
            catch
            {
                return 66;
            }

            using (readyEvent)
            {
                readyEvent.Set();
            }

            if (exitAfterReadyMilliseconds.HasValue)
            {
                await Task.Delay(exitAfterReadyMilliseconds.Value).ConfigureAwait(false);
                return 0;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return 0;
        }
        catch
        {
            return 70;
        }
    }

    private static async Task<int> IncrementAttemptAsync(string stateFile)
    {
        var path = Path.GetFullPath(stateFile);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var previousText = File.Exists(path)
            ? await File.ReadAllTextAsync(path).ConfigureAwait(false)
            : "0";
        if (!int.TryParse(previousText, NumberStyles.None, CultureInfo.InvariantCulture, out var previous) || previous < 0)
        {
            throw new InvalidDataException("The startup probe state file must contain a nonnegative attempt count.");
        }

        var attempt = checked(previous + 1);
        await File.WriteAllTextAsync(path, attempt.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        return attempt;
    }

    private static string GetRequiredValue(IReadOnlyList<string> args, string name) =>
        TryGetValue(args, name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing value for {name}.");

    private static int GetIntegerValue(IReadOnlyList<string> args, string name, int defaultValue) =>
        TryGetValue(args, name, out var value) &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : TryGetValue(args, name, out _)
                ? throw new ArgumentException($"Invalid integer for {name}.")
                : defaultValue;

    private static int? GetOptionalIntegerValue(IReadOnlyList<string> args, string name) =>
        TryGetValue(args, name, out var value)
            ? int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new ArgumentException($"Invalid integer for {name}.")
            : null;

    private static bool TryGetValue(IReadOnlyList<string> args, string name, out string value)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[index + 1];
                return true;
            }

            value = string.Empty;
            return false;
        }

        value = string.Empty;
        return false;
    }
}
