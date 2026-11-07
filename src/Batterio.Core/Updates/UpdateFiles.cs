namespace Batterio.Core.Updates;

/// <summary>
/// Swaps the installed executable for a downloaded one. Windows allows renaming an executable that
/// is in use, so the old file is moved aside to "&lt;name&gt;.old" and removed by the next launcher.
/// </summary>
public static class UpdateFiles
{
    public const string DownloadSuffix = ".download";
    public const string OldSuffix = Startup.StartupConstants.UpdateOldSuffix;
    public const string PartialSuffix = ".partial";

    public static string DownloadPathFor(string executablePath) => executablePath + DownloadSuffix;

    public static string OldPathFor(string executablePath) => executablePath + OldSuffix;

    /// <summary>Puts <paramref name="downloadPath"/> in place of <paramref name="executablePath"/>.</summary>
    public static void Swap(string executablePath, string downloadPath)
    {
        if (!File.Exists(downloadPath))
        {
            throw new UpdateException("The downloaded update is missing. Download it again.");
        }

        var old = OldPathFor(executablePath);
        if (File.Exists(old))
        {
            File.Delete(old);
        }

        File.Move(executablePath, old);
        try
        {
            File.Move(downloadPath, executablePath);
        }
        catch
        {
            File.Move(old, executablePath);
            throw;
        }
    }

    /// <summary>Undoes <see cref="Swap"/> when the new executable could not be started.</summary>
    public static void Revert(string executablePath, string downloadPath)
    {
        var old = OldPathFor(executablePath);
        if (!File.Exists(old))
        {
            return;
        }

        if (File.Exists(executablePath))
        {
            File.Move(executablePath, downloadPath, overwrite: true);
        }

        File.Move(old, executablePath);
    }

    /// <summary>Removes leftovers of earlier updates. Best effort: a file still in use stays for next time.</summary>
    public static void CleanUp(string executablePath, bool includeDownload)
    {
        TryDelete(OldPathFor(executablePath));
        TryDelete(DownloadPathFor(executablePath) + PartialSuffix);
        if (includeDownload)
        {
            TryDelete(DownloadPathFor(executablePath));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
