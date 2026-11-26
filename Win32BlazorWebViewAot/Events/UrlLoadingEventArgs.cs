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
		/// Creates the arguments with the default policy already applied: the app's own origin stays in the WebView,
		/// everything else leaves the app. A scheme that is not http or https — <c>file:</c>, <c>data:</c>, a registered
		/// protocol handler — is never internal, because rendering it here would run foreign content with this app's
		/// privileges. Applications that do want such a scheme in the WebView opt it in from <see cref="BlazorWebView.UrlLoading"/>.
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

		/// <summary>
		/// Only http(s) on the app's own host is internal. A relative URI resolves against the app's page, so it is internal
		/// as well; checking <see cref="Uri.IsAbsoluteUri"/> first also keeps this from touching <see cref="Uri.Scheme"/>,
		/// which throws for a relative URI.
		/// </summary>
		private static bool IsInternal(Uri url, Uri baseUri) =>
			!url.IsAbsoluteUri ||
			(url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) &&
			string.Equals(url.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);
	}
}
