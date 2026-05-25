using System;
using System.Threading;
using LogTest;

namespace Application
{
	class Program
	{
		static void Main(string[] args)
		{
			LogInterface logger_flush = new AsyncLogInterface();

			for (int i = 0; i < 15; i++)
			{
				logger_flush.WriteLog("Number with Flush: " + i.ToString());
				Thread.Sleep(50);
			}

			logger_flush.Stop_With_Flush();

			LogInterface logger_to_stop_without_flush = new AsyncLogInterface();

			for (int i = 50; i > 0; i--)
			{
				logger_to_stop_without_flush.WriteLog("Number with No flush: " + i.ToString());
				Thread.Sleep(20);
			}

			logger_to_stop_without_flush.Stop_Without_Flush();

			Console.ReadLine();
		}
	}
}