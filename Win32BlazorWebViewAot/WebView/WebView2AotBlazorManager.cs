// The Blazor half of a composition-hosted window.
//
// Microsoft's WebViewManager owns the renderer, IPC and static-content resolution; this class supplies the three things
// only it can: how bytes reach the browser (PostWebMessageAsString), how they arrive (WebMessageReceived), and how a
// request is answered without an HTTP server. The browser itself belongs to CompositionWebViewHost — this class borrows
// it and never disposes it, which keeps COM ownership single.

using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// A <see cref="WebViewManager"/> that renders through <see cref="ICoreWebView2CompositionController"/> using the
	/// NativeAOT-safe WebView2Aot bindings.
	/// </summary>
	internal sealed class WebView2AotBlazorManager : WebViewManager
	{
		// An IP address rather than a name so the browser skips DNS entirely; no traffic leaves the process because every
		// request to this origin is answered locally.
		private static readonly string AppHostAddress = HostAddressHelper.GetAppHostAddress();

		private static readonly Uri AppOriginUri = new($"https://{AppHostAddress}/");

		// blazor.webview.js talks to `window.external`; WebView2 exposes `window.chrome.webview`. This is the whole adapter.
		private const string BlazorWebViewAdapterScript = """
			window.external = {
				sendMessage: message => {
					window.chrome.webview.postMessage(message);
				},
				receiveMessage: callback => {
					window.chrome.webview.addEventListener('message', e => callback(e.data));
				}
			};
			""";

		// HRESULT_FROM_WIN32(ERROR_INVALID_STATE): what every WebView2 call returns once the browser has been closed,
		// e.g. because its window was destroyed without the view being disposed first.
		private const int WebViewClosedHResult = unchecked((int)0x8007139F);

		private static readonly Lock LoaderLock = new();
		private static volatile bool s_loaderLoaded;

		private readonly CompositionWebViewHost _host;
		private readonly ILogger _logger;
		private readonly string _contentRootRelativeToAppRoot;
		private readonly Action<UrlLoadingEventArgs>? _urlLoading;
		private readonly Action<BlazorWebViewInitializingEventArgs>? _blazorWebViewInitializing;
		private readonly Action<BlazorWebViewInitializedEventArgs>? _blazorWebViewInitialized;
		private readonly BlazorWebViewDeveloperTools _developerTools;
		private readonly WebView2Settings _webViewSettings;
		private readonly IServiceProvider _services;

		// Calls we forwarded to JavaScript that it never answered. When the browser goes away its replies can no longer
		// arrive, and Microsoft's renderer only refuses NEW calls at teardown — in-flight ones would hang their awaiters
		// forever (dotnet/aspnetcore#70392). We answer them ourselves, with a failure, before tearing the renderer down.
		private readonly Lock _pendingJsLock = new();
		private readonly HashSet<long> _pendingJsCallIds = [];

		// What an abandoned interop call faults with, in the component that awaited it.
		private const string InteropFaultDetail = "JavaScript interop was cancelled because the WebView was closed.";

		private CoreWebView2Events? _webViewEvents;
		private WebView2ResourceHandler? _resourceHandler;
		private WebView2NavigationHandler? _navigationHandler;
		private IComObject<ICoreWebView2Environment>? _environment;
		private bool _ownsEnvironment;
		private volatile bool _isDisposed;

		/// <summary>Set once the browser is closed for good, so later calls are dropped instead of throwing.</summary>
		private volatile bool _isBrowserClosed;

		internal WebView2AotBlazorManager(
			CompositionWebViewHost host,
			IServiceProvider services,
			Dispatcher dispatcher,
			IFileProvider fileProvider,
			JSComponentConfigurationStore jsComponents,
			string contentRootRelativeToAppRoot,
			string hostPagePathWithinFileProvider,
			Action<UrlLoadingEventArgs>? urlLoading,
			Action<BlazorWebViewInitializingEventArgs>? blazorWebViewInitializing,
			Action<BlazorWebViewInitializedEventArgs>? blazorWebViewInitialized,
			ILogger logger)
			: base(services, dispatcher, AppOriginUri, fileProvider, jsComponents, hostPagePathWithinFileProvider)
		{
			ArgumentNullException.ThrowIfNull(host);
			ArgumentNullException.ThrowIfNull(logger);

			if (services.GetService<Win32BlazorWebViewAotMarkerService>() is null)
			{
				throw new InvalidOperationException(
					"Unable to find the required services. Please add all the required services by calling " +
					$"'{nameof(IServiceCollection)}.{nameof(BlazorWebViewServiceCollectionExtensions.AddWin32BlazorWebViewAot)}' in application startup code.");
			}

			_host = host;
			_services = services;
			_logger = logger;
			_contentRootRelativeToAppRoot = contentRootRelativeToAppRoot ?? string.Empty;
			_urlLoading = urlLoading;
			_blazorWebViewInitializing = blazorWebViewInitializing;
			_blazorWebViewInitialized = blazorWebViewInitialized;
			_developerTools = services.GetRequiredService<BlazorWebViewDeveloperTools>();
			_webViewSettings = services.GetRequiredService<WebView2Settings>();
		}

		/// <summary>The environment in use, or <see langword="null"/> before startup completed.</summary>
		internal IComObject<ICoreWebView2Environment>? Environment => _environment;

		/// <summary>
		/// Creates the browser inside <see cref="CompositionWebViewHost"/>, wires the Blazor bridge and applies settings.
		/// Safe to await from any thread: every WebView2 call is marshalled back onto the host's UI thread.
		/// </summary>
		internal async Task InitializeAsync(CancellationToken cancellationToken)
		{
			if (_isDisposed)
			{
				throw new ObjectDisposedException(nameof(WebView2AotBlazorManager));
			}

			// The browser and its COM objects are affine to the window's UI thread, so all of startup runs there.
			await Dispatcher.InvokeAsync(async () => await InitializeCoreAsync(cancellationToken).ConfigureAwait(true)).ConfigureAwait(false);
		}

		private async Task InitializeCoreAsync(CancellationToken cancellationToken)
		{
			var initializingArgs = new BlazorWebViewInitializingEventArgs();
			_blazorWebViewInitializing?.Invoke(initializingArgs);

			EnsureWebView2LoaderInitialized();

			// Precedence: an environment the application handed over per view, then one shared through DI, then our own.
			_environment = initializingArgs.CoreWebView2Environment
				?? await ResolveSharedEnvironmentAsync(cancellationToken).ConfigureAwait(true)
				?? await CreateEnvironmentAsync(initializingArgs).ConfigureAwait(true);

			cancellationToken.ThrowIfCancellationRequested();
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			_logger.StartingWebView2();
			await _host.InitializeAsync(_environment).ConfigureAwait(true);

			var webView = _host.WebView ?? throw new InvalidOperationException("The browser was not created.");
			var controller = _host.Controller ?? throw new InvalidOperationException("The browser controller was not created.");

			// Settings first: the initialized event is the application's chance to override them.
			// Developer tools unlock two browser settings at once: the console itself, and the accelerator keys it
			// needs — F12 is one of the browser accelerator keys, so leaving those off would keep the console shut.
			_webViewSettings.AreDevToolsEnabled |= _developerTools.Enabled;
			_webViewSettings.AreBrowserAcceleratorKeysEnabled |= _developerTools.Enabled;
			_webViewSettings.Apply(controller, webView);

			_blazorWebViewInitialized?.Invoke(new BlazorWebViewInitializedEventArgs(
				_environment, controller, webView, _host.CompositionController));

			_navigationHandler = new WebView2NavigationHandler(AppOriginUri, _logger, _urlLoading);
			_resourceHandler = new WebView2ResourceHandler(this, _environment, _logger);

			// One registration object for the lifetime of the browser: dropping it earlier would silently unsubscribe.
			_webViewEvents = new CoreWebView2Events(webView);
			_webViewEvents.WebResourceRequested += OnWebResourceRequested;
			_webViewEvents.NavigationStarting += OnNavigationStarting;
			_webViewEvents.NewWindowRequested += OnNewWindowRequested;
			_webViewEvents.ProcessFailed += OnProcessFailed;

			WebView2ResourceHandler.RegisterFilter(webView);

			// The adapter has to exist before the document's own scripts run, hence "document created" rather than an
			// EvaluateScript after navigation. Register it before the first message can possibly arrive.
			await webView.AddScriptToExecuteOnDocumentCreatedAsync(BlazorWebViewAdapterScript).ConfigureAwait(true);

			if (_isDisposed)
			{
				return;
			}

			_webViewEvents.WebMessageReceived += OnWebMessageReceived;

			_logger.StartedWebView2();
		}

		private async ValueTask<IComObject<ICoreWebView2Environment>?> ResolveSharedEnvironmentAsync(CancellationToken cancellationToken)
		{
			if (_services.GetService<ICoreWebView2EnvironmentProvider>() is not { } provider)
			{
				return null;
			}

			// A shared environment belongs to whoever registered it.
			return await provider.GetEnvironmentAsync(cancellationToken).ConfigureAwait(true);
		}

		private async ValueTask<IComObject<ICoreWebView2Environment>> CreateEnvironmentAsync(BlazorWebViewInitializingEventArgs args)
		{
			try
			{
				var environment = await WebView2Functions.CreateCoreWebView2EnvironmentWithOptionsAsync(
					browserExecutableFolder: args.BrowserExecutableFolder,
					userDataFolder: args.UserDataFolder ?? GetDefaultUserDataFolder(),
					environmentOptions: args.EnvironmentOptions?.Object).ConfigureAwait(true)
					?? throw new InvalidOperationException("WebView2 returned no environment.");

				_ownsEnvironment = true;
				return environment;
			}
			catch (Exception exception)
			{
				_logger.FailedToCreateWebView2Environment();
				throw new InvalidOperationException(
					"Failed to create the WebView2 environment. Install the WebView2 Runtime, or point " +
					$"{nameof(BlazorWebViewInitializingEventArgs.BrowserExecutableFolder)} at a fixed-version folder.",
					exception);
			}
		}

		/// <inheritdoc />
		protected override void NavigateCore(Uri absoluteUri)
		{
			if (_isDisposed)
			{
				return;
			}

			_ = Dispatcher.InvokeAsync(() =>
			{
				if (_isDisposed || _isBrowserClosed || _host.WebView is not { } webView)
				{
					return;
				}

				_logger.NavigatingToUri(absoluteUri);
				ThrowUnlessBrowserClosed(webView.Navigate(absoluteUri.AbsoluteUri, throwOnError: false));
			});
		}

		/// <inheritdoc />
		protected override void SendMessage(string message)
		{
			if (_isDisposed || _isBrowserClosed)
			{
				return;
			}

			try
			{
				// The browser is null until initialization completes; messages sent that early are queued by the renderer,
				// not lost, so dropping the call here is harmless.
				TrackPendingJsCall(message);

				if (_host.WebView is { } webView)
				{
					ThrowUnlessBrowserClosed(webView.PostWebMessageAsString(message, throwOnError: false));
				}
			}
			catch (ObjectDisposedException)
			{
				// The window closed while a message was in flight. Nobody is left to receive it.
			}
		}

		/// <summary>
		/// Resolves static content for an intercepted request. Wraps the protected base call so
		/// <see cref="WebView2ResourceHandler"/> can stay out of this class's hierarchy.
		/// </summary>
		internal bool TryGetResponse(string uri, bool allowFallbackOnHostPage, out int statusCode, out string statusMessage, out Stream content, out IDictionary<string, string> headers) =>
			TryGetResponseContent(uri, allowFallbackOnHostPage, out statusCode, out statusMessage, out content, out headers);

		/// <inheritdoc />
		protected override async ValueTask DisposeAsyncCore()
		{
			// Release awaiting components before the renderer goes: their JavaScript replies can never arrive now, and
			// nothing else will fault them. Runs on the UI thread because MessageReceived dispatches into the renderer.
			await Dispatcher.InvokeAsync(ReleasePendingJsInteropCalls).ConfigureAwait(false);

			_isDisposed = true;

			// Base disposes the renderer, which runs component disposal and closes the IPC channel. That has to happen on
			// the UI thread and before the browser goes away, or component finalizers throw at a dead JS runtime.
			await base.DisposeAsyncCore().ConfigureAwait(true);

			await Dispatcher.InvokeAsync(ReleaseBridge).ConfigureAwait(false);
		}

		private void ReleaseBridge() => Interlocked.Exchange(ref _webViewEvents, null)?.Dispose();

		/// <summary>
		/// Answers every still-pending call into JavaScript with a failure, so its awaiter unwinds instead of hanging on a
		/// reply the dead browser can no longer send. Injects the same frames the page would have sent, through the normal
		/// receive path; the inline UI-thread dispatcher drains them before this returns.
		/// </summary>
		private void ReleasePendingJsInteropCalls()
		{
			long[] abandoned;
			lock (_pendingJsLock)
			{
				if (_pendingJsCallIds.Count == 0)
				{
					return;
				}

				abandoned = [.. _pendingJsCallIds];
				_pendingJsCallIds.Clear();
			}

			foreach (var callId in abandoned)
			{
				// The payload DotNetDispatcher.EndInvokeJS expects from the page: [callId, wasSuccessful, resultOrException].
				var invocationResult = string.Concat("[", callId.ToString(), ",false,\"", InteropFaultDetail, "\"]");

				// The framed message as blazor.webview.js would send it: type name first, then the transport's own envelope.
				// JsonEncodedText escapes the payload without reflection, so this stays NativeAOT-safe.
				var frame = string.Concat("__bwv:[\"EndInvokeJS\",", callId.ToString(), ",false,\"", JsonEncodedText.Encode(invocationResult).ToString(), "\"]");

				MessageReceived(AppOriginUri, frame);
			}
		}

		/// <summary>Records a call we forwarded to JavaScript, so teardown can answer it if the browser never will.</summary>
		private void TrackPendingJsCall(string message)
		{
			if (TryReadLeadingCallId(message, "BeginInvokeJS", out var callId))
			{
				lock (_pendingJsLock)
				{
					_pendingJsCallIds.Add(callId);
				}
			}
		}

		/// <summary>Forgets a call JavaScript answered normally, so teardown does not answer it twice.</summary>
		private void CompletePendingJsCall(string message)
		{
			if (TryReadLeadingCallId(message, "EndInvokeJS", out var callId))
			{
				lock (_pendingJsLock)
				{
					_pendingJsCallIds.Remove(callId);
				}
			}
		}

		/// <summary>
		/// Reads the call id from a framed Blazor IPC message shaped like <c>__bwv:["TypeName",123,...</c>. Hand-rolled
		/// because a full JSON parse of every frame — most of which are render batches — would cost more than tracking.
		/// </summary>
		private static bool TryReadLeadingCallId(string message, string typeName, out long callId)
		{
			var prefix = string.Concat("__bwv:[\"", typeName, "\",");
			if (!message.StartsWith(prefix, StringComparison.Ordinal))
			{
				callId = 0;
				return false;
			}

			var start = prefix.Length;
			var end = start;
			while (end < message.Length && char.IsAsciiDigit(message[end]))
			{
				end++;
			}

			callId = 0;
			return end > start && long.TryParse(message.AsSpan(start, end - start), out callId);
		}

		/// <summary>
		/// Drops the environment this view created. The owner calls it after the browser and its composition visual are
		/// gone: an environment outliving its controller is harmless, the reverse order is not.
		/// </summary>
		internal void ReleaseOwnedEnvironment()
		{
			if (_ownsEnvironment)
			{
				Interlocked.Exchange(ref _environment, null)?.Dispose();
			}
		}

		private void OnWebResourceRequested(object? sender, ICoreWebView2WebResourceRequestedEventArgs eventArgs)
		{
			if (_isDisposed || _resourceHandler is not { } handler)
			{
				return;
			}

			try
			{
				handler.Handle(eventArgs);
			}
			catch (Exception exception)
			{
				// A thrown handler would only become an HRESULT at the COM boundary; logging keeps it visible and lets the
				// browser fall back to its own handling of the request.
				_logger.LogError(exception, "Failed to answer a request for '{uri}'.", eventArgs.Request?.Uri);
			}
		}

		private void OnNavigationStarting(object? sender, ICoreWebView2NavigationStartingEventArgs eventArgs) =>
			_navigationHandler?.OnNavigationStarting(eventArgs);

		private void OnNewWindowRequested(object? sender, ICoreWebView2NewWindowRequestedEventArgs eventArgs) =>
			_navigationHandler?.OnNewWindowRequested(eventArgs);

		private void OnWebMessageReceived(object? sender, ICoreWebView2WebMessageReceivedEventArgs eventArgs)
		{
			if (_isDisposed)
			{
				return;
			}

			string? sourceUri;
			string? message;
			try
			{
				sourceUri = eventArgs.Source;
				message = eventArgs.TryGetWebMessageAsString();
			}
			catch (Exception exception)
			{
				// Page script may postMessage objects; those are not Blazor frames.
				_logger.LogDebug(exception, "Ignoring a browser message that is not a string.");
				return;
			}

			if (sourceUri is null || message is null || !Uri.TryCreate(sourceUri, UriKind.Absolute, out var source))
			{
				return;
			}

			CompletePendingJsCall(message);

			MessageReceived(source, message);
		}

		/// <summary>
		/// Raised when a browser process dies. The view turns this into its public event; nothing here decides policy.
		/// </summary>
		internal event Action<BrowserProcessFailedEventArgs>? BrowserProcessFailed;

		private void OnProcessFailed(object? sender, ICoreWebView2ProcessFailedEventArgs eventArgs)
		{
			var args = new BrowserProcessFailedEventArgs(eventArgs.ProcessFailedKind, eventArgs.ExitCode);

			_logger.LogError(
				"The WebView2 {processKind} process exited with code {exitCode}.{terminal}",
				eventArgs.ProcessFailedKind,
				eventArgs.ExitCode,
				args.IsTerminal ? " This view can no longer render." : string.Empty);

			// A renderer or browser exit means every later call into the page fails; stop sending rather than faulting each one.
			if (args.IsTerminal)
			{
				_isBrowserClosed = true;
			}

			BrowserProcessFailed?.Invoke(args);
		}

		private void ThrowUnlessBrowserClosed(HRESULT result)
		{
			if (result.Value == WebViewClosedHResult)
			{
				_isBrowserClosed = true;
				return;
			}

			result.ThrowOnError();
		}

		/// <summary>
		/// Makes sure the native WebView2Loader is loaded. WebView2Aot's P/Invokes need it by name, and a single-file or
		/// trimmed app does not always place it where the default search finds it.
		/// </summary>
		private static void EnsureWebView2LoaderInitialized()
		{
			if (s_loaderLoaded || WebView2Utilities.IsInitialized)
			{
				return;
			}

			lock (LoaderLock)
			{
				if (s_loaderLoaded || WebView2Utilities.IsInitialized)
				{
					return;
				}

				// The runtime's own search, which — unlike WebView2Utilities — knows where a self-extracting single-file app
				// unpacked its bundled native libraries.
				if (NativeLibrary.TryLoad(WebView2Utilities.LoaderDllName, typeof(WebView2Utilities).Assembly, searchPath: null, out _))
				{
					s_loaderLoaded = true;
					return;
				}

				// Then the loose files publishing leaves beside the executable: a single-file app runs from the bundle
				// while AppContext.BaseDirectory points at the extraction directory, so base-directory probes miss them.
				if (TryLoadLoaderBesideExecutable())
				{
					return;
				}

				// Then the app directory and runtimes\win-<arch>\native, including a loader embedded in the entry assembly —
				// which is where an application that embeds it itself puts it.
				var entryAssembly = Assembly.GetEntryAssembly();
				WebView2Utilities.Initialize(entryAssembly, throwOnError: false);

				if (s_loaderLoaded || WebView2Utilities.IsInitialized)
				{
					s_loaderLoaded = true;
					return;
				}

				// Then this assembly, which carries the publish architecture's loader as a resource so an AOT app can ship one
				// file. Tried last: an application's own copy is the one it chose, and its version wins.
				WebView2Utilities.Initialize(typeof(WebView2AotBlazorManager).Assembly, throwOnError: true);
			}
		}

		/// <summary>
		/// Loads the loader from next to the executable itself — its own folder and runtimes\win-&lt;arch&gt;\native.
		/// </summary>
		private static bool TryLoadLoaderBesideExecutable()
		{
			if (global::System.Environment.ProcessPath is not { Length: > 0 } exePath ||
				Path.GetDirectoryName(exePath) is not { Length: > 0 } exeDirectory)
			{
				return false;
			}

			var architecture = RuntimeInformation.ProcessArchitecture switch
			{
				Architecture.X64 => "win-x64",
				Architecture.Arm64 => "win-arm64",
				Architecture.X86 => "win-x86",
				_ => null,
			};

			string? nativeDirectory = architecture is null ? exeDirectory : Path.Combine(exeDirectory, "runtimes", architecture, "native");

			foreach (var candidate in new[] { Path.Combine(exeDirectory, WebView2Utilities.LoaderDllName), Path.Combine(nativeDirectory!, WebView2Utilities.LoaderDllName) })
			{
				if (NativeLibrary.TryLoad(candidate, out _))
				{
					s_loaderLoaded = true;
					return true;
				}
			}

			return false;
		}

		private static string? GetDefaultUserDataFolder()
		{
			// The WebView2 default sits next to the executable, which is not writable under Program Files.
			var applicationName = Assembly.GetEntryAssembly()?.GetName().Name
				?? (global::System.Environment.ProcessPath is { } processPath ? Path.GetFileNameWithoutExtension(processPath) : null);

			return string.IsNullOrEmpty(applicationName)
				? null
				: Path.Combine(
					global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData),
					$"{applicationName}.WebView2");
		}
	}
}
