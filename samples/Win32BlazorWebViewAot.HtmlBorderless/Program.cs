using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Win32BlazorWebViewAot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebView2.Utilities;

namespace Win32BlazorWebViewAot.HtmlBorderless
{
	/// <summary>
	/// The same borderless composition window as the Blazor sample, but with no Razor anywhere: the page is plain
	/// HTML + JS and drives the window through <c>chrome.webview.postMessage</c>. This is the recipe for an app that
	/// wants the host without Blazor — see wwwroot/js/app.js for the page side of the protocol.
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

			// Borderless on purpose: this sample's page draws its own title bar (see wwwroot/index.html), which only makes
			// sense when Windows is not drawing one. The default BlazorWebViewWindow has a native caption — see
			// samples/Win32BlazorWebViewAot.Html for that.
			using var window = new BorderlessBlazorWebViewWindow("Win32BlazorWebViewAot HTML Sample")
			{
				Services = provider,
				HostPage = "wwwroot/index.html",
			};

			// [JSInvokable] methods are static, so anything they need from the app is handed to them here. The logger category is
			// named by hand because a static class cannot be a generic type argument (CS0718).
			var bridgeLogger = provider.GetService<ILoggerFactory>()?.CreateLogger(nameof(DotNetBridge));
			DotNetBridge.Log = bridgeLogger is null ? null : message => bridgeLogger.LogInformation("JS called .NET: {call}", message);
			DotNetBridge.Window = window;

			// The page's top bar is the title bar: mousedown asks for a native move loop, and the caption buttons ask
			// for minimize / maximize-toggle / close. Messages arrive as plain strings on chrome.webview.postMessage, the same
			// channel blazor.webview.js uses for its own IPC frames — so anything that is not one of these five strings falls
			// through untouched. Typed calls with return values go to DotNetBridge instead of here.
			var maximized = false;
			window.BlazorWebViewInitialized += (_, e) =>
			{
				var events = new CoreWebView2Events(e.WebView);
				events.WebMessageReceived += (_, messageArgs) =>
				{
					switch (messageArgs.TryGetWebMessageAsString())
					{
						case "win:drag":
							window.BeginDrag();
							break;

						case "win:minimize":
							window.Minimize();
							break;

						case "win:maximize":
							if (maximized)
							{
								window.Restore();
							}
							else
							{
								window.Maximize();
							}

							maximized = !maximized;
							break;

						case "win:close":
							window.Close();
							break;
					}
				};
			};

			window.Show();

			// Same startup shape as the Blazor sample: StartAsync resumes on callbacks this pump delivers, so start it
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
