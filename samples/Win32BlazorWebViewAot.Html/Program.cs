using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Win32BlazorWebViewAot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Win32BlazorWebViewAot.Html
{
	/// <summary>
	/// A normal Windows window: the default <see cref="BlazorWebViewWindow"/> carries a native title bar, so Windows draws
	/// the caption and its min/max/close buttons, snap layouts and system menu — and the page below it has no drag region
	/// and no caption buttons of its own. Plain HTML + JS, like the borderless HTML sample, to keep the two easy to compare.
	/// </summary>
	internal static class Program
	{
		// The page calls DotNetBridge through [JSInvokable] methods, which Blazor finds by reflection. Nothing here references
		// those methods directly, so this attribute is what keeps them out of the trimmer — without it a Release or AOT build
		// compiles cleanly and every JavaScript call answers "not found".
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(DotNetBridge))]
		[STAThread]
		private static int Main()
		{
			var services = new ServiceCollection();
			services.AddWin32BlazorWebViewAot();
			services.AddLogging(builder =>
			{
				builder.SetMinimumLevel(LogLevel.Information);
				builder.AddSimpleConsole(options => options.SingleLine = true);
			});

#if DEBUG
			// Debug only: F12 opens DevTools and F5/Ctrl+R reloads after a JS edit. Release builds skip this, so the
			// console is unreachable and the browser keys stay off — the app owns its keyboard.
			services.AddBlazorWebViewDeveloperTools();
#endif

			var provider = services.BuildServiceProvider();

			// [JSInvokable] methods are static, so they get the logger the only way a static can: handed to it at startup. The
			// category is named by hand because a static class cannot be a generic type argument (CS0718).
			var bridgeLogger = provider.GetService<ILoggerFactory>()?.CreateLogger(nameof(DotNetBridge));
			DotNetBridge.Log = bridgeLogger is null ? null : message => bridgeLogger.LogInformation("JS called .NET: {call}", message);

			using var window = new BlazorWebViewWindow("Win32BlazorWebViewAot Title Bar Sample")
			{
				Services = provider,
				HostPage = "wwwroot/index.html",
			};

			window.Show();

			// Same startup shape as the other samples: StartAsync resumes on callbacks this pump delivers, so start it
			// and then run — never await before Run().
			var startup = window.StartAsync();
			startup.ContinueWith(
				t => Console.Error.WriteLine($"Startup failed: {t.Exception?.GetBaseException()}"),
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);

			return window.Run();
		}
	}
}
