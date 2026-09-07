using Batterio.Core;
using Batterio.Core.Abstractions;

namespace Batterio.Core.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void RotatingLogRemainsBoundedAndKeepsRecentFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Batterio.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var log = new RotatingFileLog(directory);
            for (var index = 0; index < 100; index++)
            {
                log.Log(BatterioLogLevel.Error, new string('x', 10_000));
            }

            var files = Directory.GetFiles(directory, "*.log");
            Assert.NotEmpty(files);
            Assert.True(files.Length <= 3);
            Assert.All(files, file => Assert.True(new FileInfo(file).Length <= 512 * 1024 + 10_100));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
