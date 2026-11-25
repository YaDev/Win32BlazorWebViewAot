namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Strips the parts of a request URL that are not part of the file path.
	/// </summary>
	internal static class QueryStringHelper
	{
		/// <summary>
		/// Removes query and fragment, so <c>/css/app.css?v=3</c> resolves to the file <c>css/app.css</c>. Cache busters are
		/// the normal case; a Blazor app's static assets never depend on the query.
		/// </summary>
		public static string? RemovePossibleQueryString(string? url)
		{
			if (string.IsNullOrEmpty(url))
			{
				return url;
			}

			var separator = url.IndexOfAny(['?', '#']);
			return separator < 0 ? url : url[..separator];
		}
	}
}
