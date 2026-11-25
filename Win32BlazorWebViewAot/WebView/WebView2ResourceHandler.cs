using Microsoft.Extensions.Logging;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Answers browser requests for the application's own origin out of Blazor's static content, with no HTTP server.
	/// </summary>
	internal sealed class WebView2ResourceHandler(WebView2AotBlazorManager manager, IComObject<ICoreWebView2Environment> environment, ILogger logger)
	{
		private readonly WebView2AotBlazorManager _manager = manager;
		private readonly IComObject<ICoreWebView2Environment> _environment = environment;
		private readonly ILogger _logger = logger;

		/// <summary>Asks the browser to route every request through <see cref="Handle"/>.</summary>
		public static void RegisterFilter(IComObject<ICoreWebView2> webView) =>
			webView.AddWebResourceRequestedFilter("*", COREWEBVIEW2_WEB_RESOURCE_CONTEXT.COREWEBVIEW2_WEB_RESOURCE_CONTEXT_ALL);

		/// <summary>
		/// Serves one request. Returns without touching the response when Blazor has nothing for the URI, which lets the
		/// browser handle it normally (a real network URL, for instance).
		/// </summary>
		public void Handle(ICoreWebView2WebResourceRequestedEventArgs eventArgs)
		{
			// The resource context is why the browser asked: a document request may fall back to the host page (client
			// routing), a fetch or an image must never do that.
			var context = eventArgs.ResourceContext;
			var allowHostPageFallback =
				context == COREWEBVIEW2_WEB_RESOURCE_CONTEXT.COREWEBVIEW2_WEB_RESOURCE_CONTEXT_DOCUMENT ||
				context == COREWEBVIEW2_WEB_RESOURCE_CONTEXT.COREWEBVIEW2_WEB_RESOURCE_CONTEXT_OTHER;

			using var request = eventArgs.Request;
			var rawUri = request?.Uri;
			var requestUri = QueryStringHelper.RemovePossibleQueryString(rawUri);

			_logger.HandlingWebRequest(requestUri);

			if (!_manager.TryGetResponse(requestUri!, allowHostPageFallback, out var statusCode, out var statusMessage, out var content, out var headers))
			{
				_logger.ResponseContentNotFound(requestUri);
				return;
			}

			var headerString = string.Join("\r\n", headers.Select(kvp => $"{kvp.Key}: {kvp.Value}"));

			// The browser wraps whatever it is handed in an IStream and seeks it before reading, so the stream that leaves
			// here has to answer Seek. Content from a file or an assembly resource does; content from anywhere else is
			// copied into memory first — cheap for a desktop app's assets, and it closes the source handle on the spot.
			var contentStream = new ManagedIStream(ServeSeekable(content), owned: true);

			_logger.ResponseContentBeingSent(requestUri, statusCode);

			using var reasonPhrase = new Pwstr(statusMessage);
			using var headersValue = new Pwstr(headerString);
			_environment.Object.CreateWebResourceResponse(contentStream, statusCode, reasonPhrase, headersValue, out var responseNative).ThrowOnError();

			using var response = new ComObject<ICoreWebView2WebResourceResponse>(responseNative);
			eventArgs.put_Response(response.Object).ThrowOnError();
		}

		/// <summary>
		/// Returns a stream the browser can seek, having closed <paramref name="content"/> if copying made that possible.
		/// </summary>
		private static Stream ServeSeekable(Stream content)
		{
			if (!content.CanSeek)
			{
				using (content)
				{
					var buffer = new MemoryStream();
					content.CopyTo(buffer);
					buffer.Position = 0;
					return buffer;
				}
			}

			return new AutoCloseOnReadCompleteStream(content);
		}
	}
}
