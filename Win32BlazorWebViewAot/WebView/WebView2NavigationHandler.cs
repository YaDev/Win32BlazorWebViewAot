using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Keeps the browser on the application's own origin and hands everything else to the OS.
	/// </summary>
	internal sealed class WebView2NavigationHandler(Uri appOrigin, ILogger logger, Action<UrlLoadingEventArgs>? urlLoading, IList<string> externalLaunchSchemes)
	{
		private readonly Uri _appOrigin = appOrigin;
		private readonly ILogger _logger = logger;
		private readonly Action<UrlLoadingEventArgs>? _urlLoading = urlLoading;
		private readonly IList<string> _externalLaunchSchemes = externalLaunchSchemes;

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
				TryLaunchExternalBrowser(uri);
			}

			args.Cancel = args2.UrlLoadingStrategy != UrlLoadingStrategy.OpenInWebView;
		}

		/// <summary>
		/// Handles target="_blank" and <c>window.open</c>. The request goes through the same <see cref="BlazorWebView.UrlLoading"/> decision
		/// as an ordinary navigation — defaulting to OpenExternally, because a second window is by definition outside this
		/// app — so the application can veto or reroute it. A second WebView is never created: OpenInWebView on such a
		/// request means "block". Launches are restricted to <see cref="WebView2Settings.ExternalLaunchSchemes"/>.
		/// </summary>
		public void OnNewWindowRequested(ICoreWebView2NewWindowRequestedEventArgs args)
		{
			if (!Uri.TryCreate(args.Uri, UriKind.RelativeOrAbsolute, out var uri))
			{
				return;
			}

			var args2 = new UrlLoadingEventArgs(uri) { UrlLoadingStrategy = UrlLoadingStrategy.OpenExternally };
			_urlLoading?.Invoke(args2);

			_logger.NavigationEvent(uri, args2.UrlLoadingStrategy);

			if (args2.UrlLoadingStrategy == UrlLoadingStrategy.OpenExternally)
			{
				TryLaunchExternalBrowser(uri);
			}

			// Handled either way: the popup WebView2 would otherwise create is never shown.
			args.Handled = true;
		}

		/// <summary>
		/// Shell-executes <paramref name="uri"/> only when its scheme is on the allow-list. Everything else —
		/// <c>file:</c>, <c>search-ms:</c>, custom protocol handlers — is refused here rather than reaching the OS loader.
		/// </summary>
		private void TryLaunchExternalBrowser(Uri uri)
		{
			if (!_externalLaunchSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
			{
				_logger.LogWarning(
					"Refused to launch '{uri}' externally: scheme '{scheme}' is not in {schemes}.",
					uri, uri.Scheme, string.Join(", ", _externalLaunchSchemes));
				return;
			}

			LaunchInExternalBrowser(uri);
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
