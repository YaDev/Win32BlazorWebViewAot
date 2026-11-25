namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Chooses the origin the app is served from.
	/// </summary>
	internal static class HostAddressHelper
	{
		/// <summary>
		/// A loopback literal, not a real socket: every request to this origin is intercepted before it reaches the
		/// network stack. An address instead of a name means Chromium skips DNS resolution entirely, which is why the
		/// first paint is fast.
		/// </summary>
		public static string GetAppHostAddress() => "0.0.0.1";
	}
}
