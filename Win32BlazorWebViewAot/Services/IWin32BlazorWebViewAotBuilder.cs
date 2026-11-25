using DirectN.Extensions.Com;
using Microsoft.Extensions.DependencyInjection;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Configures Win32 Blazor WebViews. Returned by
	/// <see cref="BlazorWebViewServiceCollectionExtensions.AddWin32BlazorWebViewAot(IServiceCollection)"/>.
	/// </summary>
	public interface IWin32BlazorWebViewAotBuilder
	{
		/// <summary>The service collection being configured.</summary>
		IServiceCollection Services { get; }

		/// <summary>
		/// Shares one WebView2 environment (and therefore one browser process) across every view resolved from these
		/// services, which removes most of the per-window startup cost.
		/// </summary>
		/// <remarks>
		/// The caller owns the environment: no view disposes it. WebView2 objects are thread-affine, so share only across
		/// windows that live on the same UI thread.
		/// </remarks>
		IWin32BlazorWebViewAotBuilder UseSharedCoreWebView2Environment(IComObject<ICoreWebView2Environment> environment);

		/// <inheritdoc cref="UseSharedCoreWebView2Environment(IComObject{ICoreWebView2Environment})"/>
		IWin32BlazorWebViewAotBuilder UseSharedCoreWebView2Environment(Func<IServiceProvider, CancellationToken, ValueTask<IComObject<ICoreWebView2Environment>>> factory);
	}
}
