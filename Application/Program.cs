using System;
using System.IO;
using System.Threading;
using LogTest;

namespace Application
{
	class Program
	{
		static void Main(string[] args)
		{
			string logsDirectory = Path.Combine(FindRepoRoot(), "logs");

			RunFlushDemo(logsDirectory);
			
			RunDiscardDemo(logsDirectory);
			
			Console.ReadLine();
		}

		private static void RunFlushDemo(string logsDirectory)
		{
			LogInterface logger = new AsyncLogInterface(logsDirectory);

			for (int i = 0; i < 15; i++)
			{
				logger.WriteLog("Number with Flush: " + i.ToString());
				Thread.Sleep(50);
			}

			logger.StopAndFlush();	
		}

		private static void RunDiscardDemo(string logsDirectory)
		{
			LogInterface logger = new AsyncLogInterface(logsDirectory);

			for (int i = 50; i > 0; i--)
			{
				logger.WriteLog("Number with No flush: " + i.ToString());
				Thread.Sleep(20);
			}

			logger.StopAndDiscard();	
		}
		

		private static string FindRepoRoot()
		{
			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while (dir is not null)
			{
				if (dir.GetFiles("*.sln").Length > 0)
					return dir.FullName;
				dir = dir.Parent;
			}

			throw new InvalidOperationException(
				"Could not locate repository root (no .sln found walking up from " + AppContext.BaseDirectory + ").");
		}
	}
}
