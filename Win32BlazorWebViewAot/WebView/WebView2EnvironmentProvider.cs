using DirectN.Extensions.Com;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Supplies a WebView2 environment shared by several views. Register through
	/// <see cref="IWin32BlazorWebViewAotBuilder.UseSharedCoreWebView2Environment(IComObject{ICoreWebView2Environment})"/>.
	/// </summary>
	public interface ICoreWebView2EnvironmentProvider
	{
		/// <summary>Returns the shared environment, creating it at most once.</summary>
		ValueTask<IComObject<ICoreWebView2Environment>> GetEnvironmentAsync(CancellationToken cancellationToken = default);
	}

	/// <summary>
	/// Memoizes either a ready environment or an async factory, so concurrent views initialize one browser.
	/// </summary>
	internal sealed class DefaultCoreWebView2EnvironmentProvider : ICoreWebView2EnvironmentProvider
	{
		private readonly Lazy<Task<IComObject<ICoreWebView2Environment>>> _lazy;

		public DefaultCoreWebView2EnvironmentProvider(IComObject<ICoreWebView2Environment> environment)
		{
			ArgumentNullException.ThrowIfNull(environment);
			_lazy = new Lazy<Task<IComObject<ICoreWebView2Environment>>>(() => Task.FromResult(environment), LazyThreadSafetyMode.ExecutionAndPublication);
		}

		public DefaultCoreWebView2EnvironmentProvider(IServiceProvider services, Func<IServiceProvider, CancellationToken, ValueTask<IComObject<ICoreWebView2Environment>>> factory)
		{
			ArgumentNullException.ThrowIfNull(services);
			ArgumentNullException.ThrowIfNull(factory);

			// The caller's token is deliberately not forwarded: one view cancelling would poison the cached task for
			// every other view sharing the environment.
			_lazy = new Lazy<Task<IComObject<ICoreWebView2Environment>>>(() => factory(services, CancellationToken.None).AsTask(), LazyThreadSafetyMode.ExecutionAndPublication);
		}

		public ValueTask<IComObject<ICoreWebView2Environment>> GetEnvironmentAsync(CancellationToken cancellationToken = default) => new(_lazy.Value);
	}
}
