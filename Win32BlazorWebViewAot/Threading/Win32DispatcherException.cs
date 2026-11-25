// Thrown at callers whose work could never run because the UI thread stopped pumping.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// The exception faulting dispatched work when the target UI thread no longer accepts it.
	/// </summary>
	public sealed class Win32DispatcherException(string message) : InvalidOperationException(message);
}
