using DirectN.Extensions.Com;
using Microsoft.Extensions.DependencyInjection;

namespace Win32BlazorWebViewAot
{
	internal sealed class Win32BlazorWebViewAotBuilder(IServiceCollection services) : IWin32BlazorWebViewAotBuilder
	{
		public IServiceCollection Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

		public IWin32BlazorWebViewAotBuilder UseSharedCoreWebView2Environment(IComObject<ICoreWebView2Environment> environment)
		{
			ArgumentNullException.ThrowIfNull(environment);
			Services.AddSingleton<ICoreWebView2EnvironmentProvider>(new DefaultCoreWebView2EnvironmentProvider(environment));
			return this;
		}

		public IWin32BlazorWebViewAotBuilder UseSharedCoreWebView2Environment(Func<IServiceProvider, CancellationToken, ValueTask<IComObject<ICoreWebView2Environment>>> factory)
		{
			ArgumentNullException.ThrowIfNull(factory);
			Services.AddSingleton<ICoreWebView2EnvironmentProvider>(provider => new DefaultCoreWebView2EnvironmentProvider(provider, factory));
			return this;
		}
	}
}
