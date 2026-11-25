using DirectN.Extensions.Com;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Hands the application the browser objects once they exist, before the first navigation.
	/// </summary>
	public sealed class BlazorWebViewInitializedEventArgs(
		IComObject<ICoreWebView2Environment> environment,
		IComObject<ICoreWebView2Controller> controller,
		IComObject<ICoreWebView2> webView,
		IComObject<ICoreWebView2CompositionController>? compositionController = null) : EventArgs
	{
		/// <summary>The WebView2 environment in use.</summary>
		public IComObject<ICoreWebView2Environment> Environment { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

		/// <summary>The controller, as the common base interface.</summary>
		public IComObject<ICoreWebView2Controller> Controller { get; } = controller ?? throw new ArgumentNullException(nameof(controller));

		/// <summary>The browser instance.</summary>
		public IComObject<ICoreWebView2> WebView { get; } = webView ?? throw new ArgumentNullException(nameof(webView));

		/// <summary>
		/// The composition controller this host renders through, or <see langword="null"/> for a non-composition host.
		/// </summary>
		public IComObject<ICoreWebView2CompositionController>? CompositionController { get; } = compositionController;
	}
}
