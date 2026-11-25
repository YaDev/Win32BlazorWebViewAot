using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Registers the services a Win32 Blazor Hybrid application needs.
	/// </summary>
	public static class BlazorWebViewServiceCollectionExtensions
	{
		/// <summary>
		/// Adds Microsoft's Blazor Hybrid infrastructure plus this host's WebView2 support.
		/// </summary>
		/// <example>
		/// <code>
		/// var services = new ServiceCollection();
		/// services.AddWin32BlazorWebViewAot();
		/// #if DEBUG
		/// services.AddBlazorWebViewDeveloperTools();
		/// #endif
		/// </code>
		/// </example>
		public static IWin32BlazorWebViewAotBuilder AddWin32BlazorWebViewAot(this IServiceCollection services)
		{
			ArgumentNullException.ThrowIfNull(services);

			// Microsoft's registration owns the renderer, IPC and JS component services. Duplicating them here would
			// create two sources of truth that drift apart.
			services.AddBlazorWebView();

			services.TryAddSingleton<Win32BlazorWebViewAotMarkerService>();
			services.TryAddSingleton(new BlazorWebViewDeveloperTools { Enabled = false });
			services.TryAddSingleton<WebView2Settings>();

			// Share one browser across windows with: .UseSharedCoreWebView2Environment(environment)
			return new Win32BlazorWebViewAotBuilder(services);
		}

		/// <summary>
		/// Enables browser developer tools: the console (F12) plus the browser accelerator keys, which include F12 and
		/// F5/Ctrl+R reload — a debugging session needs both to open DevTools and to refresh after a JS edit. Call it
		/// from a <c>#if DEBUG</c> block so release builds stay closed: without this call the console is unreachable and
		/// the browser keys (reload, find, print, zoom) are off, leaving text-editing shortcuts like Ctrl+C/V untouched.
		/// </summary>
		public static IServiceCollection AddBlazorWebViewDeveloperTools(this IServiceCollection services)
		{
			ArgumentNullException.ThrowIfNull(services);

			services.Replace(ServiceDescriptor.Singleton(new BlazorWebViewDeveloperTools { Enabled = true }));
			return services;
		}
	}
}
