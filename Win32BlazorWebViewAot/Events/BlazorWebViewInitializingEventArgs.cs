using DirectN.Extensions.Com;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Lets the application configure WebView2 before the browser is created.
	/// </summary>
	public sealed class BlazorWebViewInitializingEventArgs
	{
		/// <summary>
		/// Where WebView2 keeps its profile (cache, cookies). Defaults to <c>%LocalAppData%\&lt;app&gt;.WebView2</c>,
		/// because the default location under the app directory is not writable when installed to Program Files.
		/// </summary>
		public string? UserDataFolder { get; set; }

		/// <summary>A fixed-version browser folder to run from, or <see langword="null"/> for the Evergreen runtime.</summary>
		public string? BrowserExecutableFolder { get; set; }

		/// <summary>Additional environment options, or <see langword="null"/>.</summary>
		public IComObject<ICoreWebView2EnvironmentOptions>? EnvironmentOptions { get; set; }

		/// <summary>
		/// An environment the application created itself. When set it wins over the folder/options above and the view does
		/// not dispose it: the caller owns its lifetime.
		/// </summary>
		public IComObject<ICoreWebView2Environment>? CoreWebView2Environment { get; set; }
	}
}
