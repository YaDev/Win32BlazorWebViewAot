using DirectN.Extensions.Com;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// The browser chrome a desktop application usually wants off. Applied before the first navigation and overridable
	/// from the <see cref="BlazorWebView.BlazorWebViewInitialized"/> event.
	/// </summary>
	public sealed class WebView2Settings
	{
		/// <summary>Whether the browser's developer tools are reachable (F12, context menu). Debug builds only by default.</summary>
		public bool AreDevToolsEnabled { get; set; }

		/// <summary>The browser's own right-click menu. Off: a desktop app draws its own.</summary>
		public bool AreDefaultContextMenusEnabled { get; set; }

		/// <summary>The URL preview in the status bar. Off.</summary>
		public bool IsStatusBarEnabled { get; set; }

		/// <summary>
		/// Whether browser accelerator keys work — Ctrl+R/F5 reload, Ctrl+F find, Ctrl+P print, and F12 itself.
		/// Off by default so an app owns its keyboard and never looks like a browser page; text-editing shortcuts
		/// (Ctrl+C/V/X/A/Z) are unaffected. A debug session unlocks them through
		/// <see cref="BlazorWebViewServiceCollectionExtensions.AddBlazorWebViewDeveloperTools"/>, because F12 is one of
		/// these keys and a developer cannot open the console without it.
		/// </summary>
		public bool AreBrowserAcceleratorKeysEnabled { get; set; }

		/// <summary>The browser's own error page for failed navigations.</summary>
		public bool IsBuiltInErrorPageEnabled { get; set; } = true;

		/// <summary>Zoom level, 1.0 being 100%.</summary>
		public double ZoomFactor { get; set; } = 1.0;

		internal void Apply(IComObject<ICoreWebView2Controller> controller, IComObject<ICoreWebView2> webView)
		{
			using var settings = webView.Settings ?? throw new InvalidOperationException("WebView2 returned no settings.");

			settings.AreDevToolsEnabled = AreDevToolsEnabled;
			settings.AreDefaultContextMenusEnabled = AreDefaultContextMenusEnabled;
			settings.IsStatusBarEnabled = IsStatusBarEnabled;
			settings.AreBrowserAcceleratorKeysEnabled = AreBrowserAcceleratorKeysEnabled;
			settings.IsBuiltInErrorPageEnabled = IsBuiltInErrorPageEnabled;

			// Host objects are a bridge into application code: they stay off unless an app registers one explicitly.
			settings.AreHostObjectsAllowed = false;

			controller.ZoomFactor = ZoomFactor;
		}
	}
}
