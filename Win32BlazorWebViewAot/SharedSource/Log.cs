using Microsoft.Extensions.Logging;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// The structured log messages this host emits. Source-generated so it stays trim and AOT safe.
	/// </summary>
	internal static partial class Log
	{
		[LoggerMessage(1, LogLevel.Debug, "Creating a file provider for content root '{contentRoot}' serving host page '{hostPage}'", EventName = nameof(CreatingFileProvider))]
		public static partial void CreatingFileProvider(this ILogger logger, string contentRoot, string hostPage);

		[LoggerMessage(2, LogLevel.Debug, "Adding root component '{componentType}' at selector '{selector}' with {parameterCount} parameters", EventName = nameof(AddingRootComponent))]
		public static partial void AddingRootComponent(this ILogger logger, string componentType, string selector, int parameterCount);

		[LoggerMessage(3, LogLevel.Debug, "Starting initial navigation to '{startPath}'", EventName = nameof(StartingInitialNavigation))]
		public static partial void StartingInitialNavigation(this ILogger logger, string startPath);

		[LoggerMessage(4, LogLevel.Debug, "Navigating WebView to '{uri}'", EventName = nameof(NavigatingToUri))]
		public static partial void NavigatingToUri(this ILogger logger, Uri uri);

		[LoggerMessage(5, LogLevel.Trace, "Handling web request for '{uri}'", EventName = nameof(HandlingWebRequest))]
		public static partial void HandlingWebRequest(this ILogger logger, string? uri);

		[LoggerMessage(6, LogLevel.Trace, "Sending response content for '{uri}' with status {statusCode}", EventName = nameof(ResponseContentBeingSent))]
		public static partial void ResponseContentBeingSent(this ILogger logger, string? uri, int statusCode);

		[LoggerMessage(7, LogLevel.Warning, "No content found for request '{uri}'", EventName = nameof(ResponseContentNotFound))]
		public static partial void ResponseContentNotFound(this ILogger logger, string? uri);

		[LoggerMessage(8, LogLevel.Debug, "Navigation to '{uri}' will {strategy}", EventName = nameof(NavigationEvent))]
		public static partial void NavigationEvent(this ILogger logger, Uri uri, UrlLoadingStrategy strategy);

		[LoggerMessage(9, LogLevel.Information, "Launching '{uri}' in an external browser", EventName = nameof(LaunchExternalBrowser))]
		public static partial void LaunchExternalBrowser(this ILogger logger, Uri uri);

		[LoggerMessage(10, LogLevel.Debug, "Starting WebView2", EventName = nameof(StartingWebView2))]
		public static partial void StartingWebView2(this ILogger logger);

		[LoggerMessage(11, LogLevel.Debug, "WebView2 started", EventName = nameof(StartedWebView2))]
		public static partial void StartedWebView2(this ILogger logger);

		[LoggerMessage(12, LogLevel.Error, "Failed to create the WebView2 environment. Is the WebView2 Runtime installed?", EventName = nameof(FailedToCreateWebView2Environment))]
		public static partial void FailedToCreateWebView2Environment(this ILogger logger);
	}
}
