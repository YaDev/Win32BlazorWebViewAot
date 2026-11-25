namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Where a link should open.
	/// </summary>
	public enum UrlLoadingStrategy
	{
		/// <summary>Opens inside this WebView.</summary>
		OpenInWebView,

		/// <summary>Opens in the OS default handler (a browser for http/https), outside this app.</summary>
		OpenExternally,

		/// <summary>Cancels the navigation without opening it anywhere.</summary>
		Hide,
	}
}
