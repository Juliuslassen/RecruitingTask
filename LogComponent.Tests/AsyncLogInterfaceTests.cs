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
            logger.StopAndFlush();

            // StopAndFlush Joins the consumer thread, so the line is on disk by the time it returns.
            logDir.Refresh();
            var file = logDir.GetFiles("*.log").Single();
            var content = ReadAll(file);

            Assert.Contains(message, content);
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

            logger.StopAndFlush();

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

            logger.StopAndFlush();

            // StopAndFlush Joins the consumer thread, so every produced message is on disk
            // by the time it returns — no polling needed.
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

    [Fact]
    public void Stop_With_Flush_BlocksUntilAllOutstandingLogsAreWritten()
    {
        // README requirement #3 (drain variant): the call must not return until every
        // queued log has been written. We burst-enqueue so the consumer has a real
        // backlog to drain, then read the file with NO polling — if Stop_With_Flush
        // is honoring its contract, every message is already on disk.
        const int messageCount = 1000;

        var logDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), "LogComponentTests_" + Guid.NewGuid().ToString("N")));

        try
        {
            var logger = new AsyncLogInterface(logDir.FullName);

            for (int i = 0; i < messageCount; i++)
                logger.WriteLog($"flush-line-{i}");

            logger.StopAndFlush();

            // No WaitUntil here — the contract is "call returns when drain is done."
            logDir.Refresh();
            var file = logDir.GetFiles("*.log").Single();
            var content = ReadAll(file);
            var actualLines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;

            Assert.Equal(messageCount, actualLines);
        }
        finally
        {
            if (logDir.Exists)
                Directory.Delete(logDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Stop_Without_Flush_DiscardsOutstandingLogs()
    {
        // README requirement #3 (immediate-stop variant): outstanding logs are discarded.
        // Burst-enqueue many messages so the queue still has items when we call stop,
        // then assert the file contains fewer lines than were produced.
        const int messageCount = 1000;

        var logDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), "LogComponentTests_" + Guid.NewGuid().ToString("N")));

        try
        {
            var logger = new AsyncLogInterface(logDir.FullName);

            for (int i = 0; i < messageCount; i++)
                logger.WriteLog($"noflush-line-{i}");

            logger.StopAndDiscard();

            // After Stop_Without_Flush returns, the consumer thread is fully stopped
            // and the file is in a stable state (the call Joins the thread).
            logDir.Refresh();
            var file = logDir.GetFiles("*.log").Single();
            var content = ReadAll(file);
            var actualLines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;

            // Lower bound is 1, not 0: the consumer must have done *some* work before stopping.
            // A broken StopAndDiscard that cancels instantly before writing anything should fail.
            Assert.InRange(actualLines, 1, messageCount - 1);
        }
        finally
        {
            if (logDir.Exists)
                Directory.Delete(logDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void WriteLog_AfterStop_SilentlyDrops()
    {
        // Locks in current behaviour: writes after Stop are silently dropped (the implementation
        // checks _lines.IsAddingCompleted and returns). Must not throw, and the dropped message
        // must not appear in the file.
        const string preStop = "pre-stop";
        const string postStop = "post-stop";

        var logDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), "LogComponentTests_" + Guid.NewGuid().ToString("N")));

        try
        {
            var logger = new AsyncLogInterface(logDir.FullName);

            logger.WriteLog(preStop);
            logger.StopAndFlush();

            // No exception expected.
            logger.WriteLog(postStop);

            logDir.Refresh();
            var file = logDir.GetFiles("*.log").Single();
            var content = ReadAll(file);

            Assert.Contains(preStop, content);
            Assert.DoesNotContain(postStop, content);
        }
        finally
        {
            if (logDir.Exists)
                Directory.Delete(logDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Stop_CalledMultipleTimesInAnyOrder_DoesNotThrow()
    {
        // Idempotency check: the Interlocked.Exchange gate must make repeat / mixed-order
        // stop calls safe. We exercise StopAndFlush twice, then StopAndDiscard, and expect
        // no exceptions.
        var logDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), "LogComponentTests_" + Guid.NewGuid().ToString("N")));

        try
        {
            var logger = new AsyncLogInterface(logDir.FullName);

            logger.WriteLog("some line");

            logger.StopAndFlush();
            logger.StopAndFlush();
            logger.StopAndDiscard();
        }
        finally
        {
            if (logDir.Exists)
                Directory.Delete(logDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Stop_DiscardThenFlush_DoesNotThrow()
    {
        // Reverse-order idempotency check: StopAndDiscard first, then StopAndFlush.
        var logDir = new DirectoryInfo(
            Path.Combine(Path.GetTempPath(), "LogComponentTests_" + Guid.NewGuid().ToString("N")));

        try
        {
            var logger = new AsyncLogInterface(logDir.FullName);

            logger.WriteLog("some line");

            logger.StopAndDiscard();
            logger.StopAndFlush();
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
