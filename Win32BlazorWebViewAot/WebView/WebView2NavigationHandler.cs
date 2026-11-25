using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Keeps the browser on the application's own origin and hands everything else to the OS.
	/// </summary>
	internal sealed class WebView2NavigationHandler(Uri appOrigin, ILogger logger, Action<UrlLoadingEventArgs>? urlLoading)
	{
		private readonly Uri _appOrigin = appOrigin;
		private readonly ILogger _logger = logger;
		private readonly Action<UrlLoadingEventArgs>? _urlLoading = urlLoading;

		public void OnNavigationStarting(ICoreWebView2NavigationStartingEventArgs args)
		{
			if (!Uri.TryCreate(args.Uri, UriKind.RelativeOrAbsolute, out var uri))
			{
				return;
			}

			var args2 = UrlLoadingEventArgs.CreateWithDefaultLoadingStrategy(uri, _appOrigin);
			_urlLoading?.Invoke(args2);

			_logger.NavigationEvent(uri, args2.UrlLoadingStrategy);

			if (args2.UrlLoadingStrategy == UrlLoadingStrategy.OpenExternally)
			{
				LaunchInExternalBrowser(uri);
			}

			args.Cancel = args2.UrlLoadingStrategy != UrlLoadingStrategy.OpenInWebView;
		}

		/// <summary>Handles target="_blank": the new window is always the user's real browser.</summary>
		public void OnNewWindowRequested(ICoreWebView2NewWindowRequestedEventArgs args)
		{
			if (!Uri.TryCreate(args.Uri, UriKind.RelativeOrAbsolute, out var uri))
			{
				return;
			}

			LaunchInExternalBrowser(uri);
			args.Handled = true;
		}

		private void LaunchInExternalBrowser(Uri uri)
		{
			_logger.LaunchExternalBrowser(uri);

			try
			{
				using var process = new Process();
				process.StartInfo.UseShellExecute = true;
				process.StartInfo.FileName = uri.ToString();
				process.Start();
			}
			catch (Exception exception)
			{
				// No handler for the scheme, or the shell refused. The navigation is already cancelled either way.
				_logger.LogWarning(exception, "Failed to launch '{uri}' externally.", uri);
			}
		}
	}
}
