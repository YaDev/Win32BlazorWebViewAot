namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Reports that a WebView2 browser process exited or stopped working.
	/// </summary>
	/// <remarks>
	/// A composition-hosted page cannot recover by itself: there is no child window to repaint, and every Blazor call into
	/// the renderer fails once that renderer is gone. The host therefore reports it instead of pretending the app is alive.
	/// </remarks>
	public sealed class BrowserProcessFailedEventArgs(COREWEBVIEW2_PROCESS_FAILED_KIND processFailedKind, int exitCode) : EventArgs
	{
		/// <summary>Which process failed and how.</summary>
		public COREWEBVIEW2_PROCESS_FAILED_KIND ProcessFailedKind { get; } = processFailedKind;

		/// <summary>The process exit code, or 0 when the runtime did not report one.</summary>
		public int ExitCode { get; } = exitCode;

		/// <summary>
		/// True when this view can never render again (the browser process itself, or the renderer holding this page, is
		/// gone). False for a frame or GPU process failure, which the runtime recovers from on its own.
		/// </summary>
		public bool IsTerminal =>
			ProcessFailedKind is COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED
				or COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED;

		/// <inheritdoc />
		public override string ToString() => $"{ProcessFailedKind} (exit code {ExitCode})";
	}
}
