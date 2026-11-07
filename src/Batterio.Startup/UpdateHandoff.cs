using System.Diagnostics;
using System.Globalization;
using Batterio.Core.Startup;

namespace Batterio.Startup;

/// <summary>
/// Finishes an in-place update: the copy that replaced Batterio.exe starts the new file with
/// <see cref="StartupConstants.UpdateWaitArgument"/> and its own process id, then exits. The new
/// launcher waits for it, so the single-instance check and the cleanup of the previous unpacked
/// runtime both see it gone, and then removes the "&lt;exe&gt;.old" file it was renamed to.
/// </summary>
public static class UpdateHandoff
{
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Removes the update argument pair and returns the process id it named, if any.</summary>
    public static string[] TakeWaitProcessId(IReadOnlyList<string> arguments, out int? processId)
    {
        processId = null;
        var remaining = new List<string>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], StartupConstants.UpdateWaitArgument, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < arguments.Count &&
                    int.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                    id > 0)
                {
                    processId = id;
                    index++;
                }

                continue;
            }

            remaining.Add(arguments[index]);
        }

        return remaining.ToArray();
    }

    /// <summary>Waits for <paramref name="processId"/> to exit. Returns false when it is still running.</summary>
    public static bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            // Already gone.
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Deletes the previous executable left beside <paramref name="launcherPath"/>; best effort.</summary>
    public static void RemovePreviousExecutable(string launcherPath)
    {
        var old = launcherPath + StartupConstants.UpdateOldSuffix;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(old))
                {
                    return;
                }

                File.Delete(old);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}
