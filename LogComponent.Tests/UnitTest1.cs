using System;
using System.IO;
using System.Linq;
using System.Threading;
using LogTest;
using Microsoft.Extensions.Time.Testing;

namespace LogComponent.Tests;

public class AsyncLogInterfaceTests
{
    [Fact]
    public void WriteLog_WritesMessageToFile()
    {
        const string message = "Hello from the first test";
        var logDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), "LogComponentTests_" + Guid.NewGuid().ToString("N")));

        try
        {
            var logger = new AsyncLogInterface(logDir.FullName);
            logger.WriteLog(message);
            logger.Stop_With_Flush();

            // Stop_With_Flush is not currently synchronous; poll until the line lands or we time out.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            string? content = null;
            while (DateTime.UtcNow < deadline)
            {
                logDir.Refresh();
                var newest = logDir.Exists
                    ? logDir.GetFiles("*.log").OrderByDescending(f => f.CreationTimeUtc).FirstOrDefault()
                    : null;

                if (newest is not null)
                {
                    using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    content = reader.ReadToEnd();
                    if (content.Contains(message))
                        break;
                }

                Thread.Sleep(50);
            }

            Assert.NotNull(content);
            Assert.Contains(message, content!);
        }
        finally
        {
            if (logDir.Exists)
                Directory.Delete(logDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void WriteLog_RollsOverToNewFile_WhenMidnightCrossed()
    {
        const string beforeMidnight = "line before midnight";
        const string afterMidnight = "line after midnight";

        // Start one second before midnight so a tiny advance crosses the date boundary.
        var startTime = new DateTimeOffset(2026, 1, 1, 23, 59, 59, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(startTime);

        var logDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), "LogComponentTests_" + Guid.NewGuid().ToString("N")));

        try
        {
            var logger = new AsyncLogInterface(logDir.FullName, fakeTime);

            logger.WriteLog(beforeMidnight);

            // Wait for the worker to flush the pre-midnight line so the first file's contents are stable.
            WaitUntil(() => AnyFileContains(logDir, beforeMidnight), TimeSpan.FromSeconds(5));

            // Cross midnight, then write — the rollover check fires per-line, so it triggers when this one is consumed.
            fakeTime.Advance(TimeSpan.FromSeconds(2));
            logger.WriteLog(afterMidnight);

            logger.Stop_With_Flush();

            WaitUntil(() => AnyFileContains(logDir, afterMidnight), TimeSpan.FromSeconds(5));

            logDir.Refresh();
            var files = logDir.GetFiles("*.log").OrderBy(f => f.Name).ToArray();

            Assert.Equal(2, files.Length);

            // Filenames are derived from the injected clock, so the dates are deterministic.
            Assert.Contains("20260101", files[0].Name);
            Assert.Contains("20260102", files[1].Name);

            Assert.Contains(beforeMidnight, ReadAll(files[0]));
            Assert.DoesNotContain(afterMidnight, ReadAll(files[0]));

            Assert.Contains(afterMidnight, ReadAll(files[1]));
            Assert.DoesNotContain(beforeMidnight, ReadAll(files[1]));
        }
        finally
        {
            if (logDir.Exists)
                Directory.Delete(logDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void WriteLog_FromManyThreads_NoMessagesAreLost()
    {
        // Regression guard for the List<LogLine> race: many concurrent producers must not
        // drop messages or crash the consumer thread.
        const int threadCount = 8;
        const int messagesPerThread = 250;
        const int totalMessages = threadCount * messagesPerThread;

        var logDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), "LogComponentTests_" + Guid.NewGuid().ToString("N")));

        try
        {
            var logger = new AsyncLogInterface(logDir.FullName);

            var threads = new Thread[threadCount];
            for (int t = 0; t < threadCount; t++)
            {
                int threadId = t;
                threads[t] = new Thread(() =>
                {
                    for (int i = 0; i < messagesPerThread; i++)
                        logger.WriteLog($"T{threadId}-M{i}");
                });
            }

            foreach (var th in threads) th.Start();
            foreach (var th in threads) th.Join();

            logger.Stop_With_Flush();

            // Stop_With_Flush still returns before drain finishes (test #3 territory), so poll
            // until the file has enough lines to cover every produced message.
            WaitUntil(() => CountLogLines(logDir) >= totalMessages, TimeSpan.FromSeconds(15));

            logDir.Refresh();
            var file = logDir.GetFiles("*.log").Single();
            var content = ReadAll(file);

            var missing = new List<string>();
            for (int t = 0; t < threadCount; t++)
                for (int i = 0; i < messagesPerThread; i++)
                {
                    var token = $"T{t}-M{i}";
                    if (!content.Contains(token))
                        missing.Add(token);
                }

            Assert.True(
                missing.Count == 0,
                $"{missing.Count}/{totalMessages} messages missing. First few: {string.Join(", ", missing.Take(10))}");
        }
        finally
        {
            if (logDir.Exists)
                Directory.Delete(logDir.FullName, recursive: true);
        }
    }

    private static int CountLogLines(DirectoryInfo dir)
    {
        dir.Refresh();
        if (!dir.Exists) return 0;
        int total = 0;
        foreach (var file in dir.GetFiles("*.log"))
        {
            // Subtract 1 for the per-file header row.
            var lines = ReadAll(file).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
            if (lines > 0) total += lines;
        }
        return total;
    }

    private static bool AnyFileContains(DirectoryInfo dir, string needle)
    {
        dir.Refresh();
        if (!dir.Exists) return false;
        foreach (var file in dir.GetFiles("*.log"))
        {
            if (ReadAll(file).Contains(needle)) return true;
        }
        return false;
    }

    private static string ReadAll(FileInfo file)
    {
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            Thread.Sleep(25);
        }
    }
}
