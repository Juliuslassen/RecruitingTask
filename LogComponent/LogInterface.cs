namespace LogTest
{
	public interface LogInterface
	{
		/// <summary>
		/// Stop the logging. If any outstadning logs theses will not be written to Log
		/// </summary>
		void StopAndDiscard();

		/// <summary>
		/// Stop the logging. The call will not return until all all logs have been written to Log.
		/// </summary>
		void StopAndFlush();

		/// <summary>
		/// WriteLog a message to the Log.
		/// </summary>
		/// <param name="s">The s to written to the log</param>
		void WriteLog(string s);
	}
}