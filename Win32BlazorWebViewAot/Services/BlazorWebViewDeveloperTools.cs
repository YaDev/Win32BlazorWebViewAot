namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Whether browser developer tools are enabled. Registered by
	/// <see cref="BlazorWebViewServiceCollectionExtensions.AddBlazorWebViewDeveloperTools"/>.
	/// </summary>
	public sealed class BlazorWebViewDeveloperTools
	{
		/// <summary>
		/// Defaults to <see langword="false"/>, so a release build never exposes the browser console.
		/// Opt in from a <c>#if DEBUG</c> block with
		/// <see cref="BlazorWebViewServiceCollectionExtensions.AddBlazorWebViewDeveloperTools"/>.
		/// </summary>
		public bool Enabled { get; set; }
	}
}
