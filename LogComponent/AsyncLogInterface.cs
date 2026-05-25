namespace LogTest
{
	using System;
	using System.Collections.Concurrent;
	using System.IO;
	using System.Text;
	using System.Threading;

	public class AsyncLogInterface : LogInterface, IDisposable
	{
		private static int _instanceCounter;

		private readonly string _logDirectory;
		private readonly TimeProvider _timeProvider;
		private readonly BlockingCollection<LogLine> _lines = new BlockingCollection<LogLine>();
		private readonly CancellationTokenSource _cts = new CancellationTokenSource();
		private readonly Thread _runThread;
		private readonly int _instanceId = Interlocked.Increment(ref _instanceCounter);
		private int _stopRequested;
		private int _disposed;

		// Mutated and read only from the consumer thread (MainLoop).
		// Not safe to access from any other thread.
		private StreamWriter _writer;

		// Mutated and read only from the consumer thread (MainLoop).
		// Not safe to access from any other thread.
		private DateTime _curDate;

		public AsyncLogInterface(string logDirectory, TimeProvider? timeProvider = null)
		{
			if (string.IsNullOrWhiteSpace(logDirectory))
				throw new ArgumentException("Log directory must be provided.", nameof(logDirectory));

			this._logDirectory = logDirectory;
			this._timeProvider = timeProvider ?? TimeProvider.System;

			if (!Directory.Exists(this._logDirectory))
				Directory.CreateDirectory(this._logDirectory);

			this._curDate = this._timeProvider.GetLocalNow().DateTime;
			this._writer = OpenNewWriter();

			this._runThread = new Thread(this.MainLoop) { IsBackground = true };
			this._runThread.Start();
		}

		public void WriteLog(string s)
		{
			if (this._lines.IsAddingCompleted)
				return;

			try
			{
				this._lines.Add(new LogLine { Text = s, Timestamp = this._timeProvider.GetLocalNow().DateTime });
			}
			catch (InvalidOperationException)
			{
				// CompleteAdding was called between the check above and Add — drop silently.
			}
		}

		public void StopAndDiscard()
		{
			if (Interlocked.Exchange(ref this._stopRequested, 1) != 0)
				return;

			this._lines.CompleteAdding();
			this._cts.Cancel();
			this._runThread.Join();
		}

		public void StopAndFlush()
		{
			if (Interlocked.Exchange(ref this._stopRequested, 1) != 0)
				return;

			this._lines.CompleteAdding();
			this._runThread.Join();
		}

		/// <summary>
		/// Stops the logger (draining any outstanding logs) and releases the
		/// queue + cancellation-token resources. Safe to call multiple times.
		/// Equivalent to <see cref="StopAndFlush"/> followed by disposal of
		/// internally-owned <see cref="IDisposable"/> fields.
		/// </summary>
		public void Dispose()
		{
			if (Interlocked.Exchange(ref this._disposed, 1) != 0)
				return;

			// Idempotent: if the caller already stopped us, this is a no-op
			// and the thread is already joined.
			StopAndFlush();

			this._cts.Dispose();
			this._lines.Dispose();

			GC.SuppressFinalize(this);
		}

		private void MainLoop()
		{
			try
			{
				foreach (LogLine logLine in this._lines.GetConsumingEnumerable(this._cts.Token))
				{
					TryProcessLogLine(logLine);
				}
			}
			catch (OperationCanceledException)
			{
				// StopAndDiscard — outstanding logs are dropped as the contract requires.
			}
			finally
			{
				this._writer.Dispose();
			}
		}

		private void TryProcessLogLine(LogLine logLine)
		{
			try
			{
				RotateWriterIfDateChanged();
				WriteFormattedLine(logLine);
			}
			catch (Exception ex)
			{
				// Requirement #4: a failed write must not take down the consumer thread.
				// Losing this one line is preferable to silently losing every line that follows.
				Console.Error.WriteLine(
					$"AsyncLogInterface: dropping log line — {ex.GetType().Name}: {ex.Message}");
			}
		}

		private void RotateWriterIfDateChanged()
		{
			DateTime now = this._timeProvider.GetLocalNow().DateTime;
			if (now.Date == this._curDate.Date) return;

			// Open the new writer before disposing the old one, so a failure here
			// leaves us with a working writer rather than a disposed one.
			StreamWriter newWriter = OpenNewWriter();
			StreamWriter oldWriter = this._writer;
			this._writer = newWriter;
			this._curDate = now;
			oldWriter.Dispose();
		}

		private void WriteFormattedLine(LogLine logLine)
		{
			var sb = new StringBuilder();
			sb.Append(logLine.Timestamp.ToString("yyyy-MM-dd HH:mm:ss:fff"));
			sb.Append('\t');
			sb.Append(logLine.LineText());
			sb.Append('\t');
			sb.Append(Environment.NewLine);

			this._writer.Write(sb.ToString());
		}

		private StreamWriter OpenNewWriter()
		{
			var path = Path.Combine(
				this._logDirectory,
				"Log" + this._timeProvider.GetLocalNow().DateTime.ToString("yyyyMMdd HHmmss fff")
					+ "_" + this._instanceId.ToString() + ".log");

			var writer = File.AppendText(path);
			writer.Write("Timestamp".PadRight(25, ' ') + "\t" + "Data".PadRight(15, ' ') + "\t" + Environment.NewLine);
			writer.AutoFlush = true;
			return writer;
		}
	}
}
