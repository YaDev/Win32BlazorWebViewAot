namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Carries an exception thrown by work Blazor dispatched to the UI thread.
	/// </summary>
	public sealed class BlazorWebViewUnhandledExceptionEventArgs(Exception exception) : EventArgs
	{
		/// <summary>The exception.</summary>
		public Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));

		/// <summary>
		/// Set it to keep the process alive. Otherwise the exception is promoted to an unhandled exception, which is what
		/// a crash reporter or debugger expects for a real bug.
		/// </summary>
		public bool Handled { get; set; }
	}
}
