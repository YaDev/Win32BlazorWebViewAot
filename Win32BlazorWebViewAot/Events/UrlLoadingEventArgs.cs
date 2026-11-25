namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Describes a navigation about to happen, and lets the application redirect it.
	/// </summary>
	public sealed class UrlLoadingEventArgs(Uri url) : EventArgs
	{
		/// <summary>The URL being loaded.</summary>
		public Uri Url { get; } = url ?? throw new ArgumentNullException(nameof(url));

		/// <summary>
		/// How to load <see cref="Url"/>. The default puts the app's own origin in the WebView and everything else in an
		/// external browser, which is what a desktop application normally wants.
		/// </summary>
		public UrlLoadingStrategy UrlLoadingStrategy { get; set; }

		/// <summary>
		/// Creates the arguments with the default policy already applied: same origin stays in the WebView, anything else
		/// leaves the app.
		/// </summary>
		public static UrlLoadingEventArgs CreateWithDefaultLoadingStrategy(Uri url, Uri baseUri)
		{
			ArgumentNullException.ThrowIfNull(url);
			ArgumentNullException.ThrowIfNull(baseUri);

			var args = new UrlLoadingEventArgs(url)
			{
				UrlLoadingStrategy = IsInternal(url, baseUri) ? UrlLoadingStrategy.OpenInWebView : UrlLoadingStrategy.OpenExternally,
			};

			return args;
		}

		private static bool IsInternal(Uri url, Uri baseUri) =>
			url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps ||
			string.Equals(url.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);
	}
}
