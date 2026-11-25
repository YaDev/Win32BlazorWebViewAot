using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Win32BlazorWebViewAot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Win32BlazorWebViewAot.BlazorBorderless
{
	internal static class Program
	{
		// The host page calls DotNetBridge through a [JSInvokable] method, which Blazor finds by reflection. Nothing here
		// references it directly, so this attribute is what keeps it out of the trimmer — without it a Release or AOT build
		// compiles cleanly and every JavaScript call answers "not found".
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(DotNetBridge))]
		[STAThread]
		private static int Main()
		{
			var services = CreateServices();

			// [JSInvokable] methods are static, so the logger is handed to them at startup. The category is named by hand because
			// a static class cannot be a generic type argument (CS0718). That log line is also how this interop path is verified.
			var bridgeLogger = services.GetService<ILoggerFactory>()?.CreateLogger(nameof(DotNetBridge));
			DotNetBridge.Log = bridgeLogger is null ? null : message => bridgeLogger.LogInformation("JS called .NET: {call}", message);

			// Borderless on purpose: this sample's Razor UI draws its own title bar (the AppWindow holder below exists to
			// drive it), which only makes sense when Windows is not drawing one. For the native caption see
			// samples/Win32BlazorWebViewAot.Html.
			using var window = new BorderlessBlazorWebViewWindow("Win32BlazorWebViewAot Sample")
			{
				Services = services,
				HostPage = "wwwroot/index.html",
			};

			// The title bar drives the window through this holder; components inject it.
			services.GetRequiredService<AppWindow>().Attach(window);

			window.RootComponents.Add<App>("#app");

			window.UnhandledException += (_, eventArgs) =>
			{
				var logger = services.GetService<ILogger<BlazorWebView>>();
				logger?.LogCritical(eventArgs.Exception, "Unhandled exception in dispatched work.");

				// Keep the window alive so a component bug is readable instead of closing the app.
				eventArgs.Handled = true;
			};

			window.Show();

			// StartAsync awaits WebView2 creation, and those COM callbacks are delivered by this thread's message loop —
			// so it must not be awaited before Run(). The queue's synchronization context resumes it inside the pump.
			var startup = window.StartAsync();
			startup.ContinueWith(
				t => services.GetService<ILogger<BlazorWebView>>()?.LogCritical(t.Exception, "Startup failed."),
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);

			var exitCode = window.Run();

			// Run() returned because the HWND is gone; surface a startup failure as a non-zero exit code.
			return startup.IsFaulted ? 1 : exitCode;
		}

		private static IServiceProvider CreateServices()
		{
			var services = new ServiceCollection();

			services.AddSingleton<AppWindow>();
			services.AddWin32BlazorWebViewAot();
			services.AddLogging(builder =>
			{
				builder.SetMinimumLevel(LogLevel.Debug);
				builder.AddDebug();
				builder.AddSimpleConsole(options => options.SingleLine = true);
			});

#if DEBUG
			services.AddBlazorWebViewDeveloperTools();
#endif

			return services.BuildServiceProvider();
		}
	}
}
