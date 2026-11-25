// The public entry point of the library: a Razor-component view inside a borderless Win32 window.
//
// Everything above this line is configuration; everything below it is machinery. The view owns three things and their
// teardown order — the dispatcher, the composition host, and the WebView manager — because getting that order wrong is
// how a desktop Blazor app produces a finalizer crash on exit instead of a clean shutdown.

using System.Collections.Specialized;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Hosts Razor components in a WebView2 rendered through the composition tree of a <see cref="Win32Window"/>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Create, start and dispose the view on the window's UI thread, which must be an STA thread running a message loop.
	/// Use <see cref="BlazorWebViewWindow"/> to get a window that drives the view for you.
	/// </para>
	/// <para>
	/// A view attached directly to a hand-written <see cref="Win32Window"/> tracks nothing by itself: the composition host
	/// follows the window's client area, focus and input through <see cref="Win32Window.AttachComposition"/>, but a move
	/// notification still has to be forwarded from <see cref="Win32Window.OnMoved"/>.
	/// </para>
	/// </remarks>
	public class BlazorWebView : IAsyncDisposable
	{
		private readonly Win32MessageQueue _queue;
		private readonly Win32Dispatcher _dispatcher;
		private readonly CompositionWebViewHost _compositionHost;

		private WebView2AotBlazorManager? _manager;
		private ILogger _logger = NullLogger<BlazorWebView>.Instance;
		private string? _hostPage;
		private string _startPath = "/";
		private IServiceProvider? _services;
		private Task? _startTask;
		private Task? _disposeTask;

		/// <summary>
		/// Creates a view that renders into <paramref name="window"/>. Must run on the window's thread.
		/// </summary>
		/// <param name="window">The window whose composition tree hosts the browser.</param>
		/// <remarks>
		/// Work for the UI thread goes through the thread's <see cref="Win32MessageQueue"/>, created on demand. Several
		/// views on one thread share that queue and therefore its synchronization context.
		/// </remarks>
		/// <exception cref="InvalidOperationException">The calling thread is not STA.</exception>
		public BlazorWebView(Win32Window window)
			: this(window, Win32MessageQueue.GetOrCreateForCurrentThread())
		{
		}

		/// <summary>
		/// Creates a view whose UI-thread work goes through <paramref name="queue"/> instead of the calling thread's own.
		/// </summary>
		/// <param name="window">The window whose composition tree hosts the browser.</param>
		/// <param name="queue">The queue of the thread that owns <paramref name="window"/>.</param>
		/// <exception cref="ArgumentException"><paramref name="queue"/> belongs to another thread.</exception>
		public BlazorWebView(Win32Window window, Win32MessageQueue queue)
		{
			ArgumentNullException.ThrowIfNull(window);
			ArgumentNullException.ThrowIfNull(queue);

			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
			{
				throw new InvalidOperationException("A BlazorWebView must be created on an STA thread. Mark the entry point with [STAThread].");
			}

			if (!queue.CheckAccess())
			{
				throw new ArgumentException("The message queue must belong to the thread that owns the window.", nameof(queue));
			}

			_queue = queue;
			_queue.UnhandledException += HandleQueueUnhandledException;
			_dispatcher = new Win32Dispatcher(_queue, HandleDispatchedFailure);

			// WebView2's COM objects are thread-affine, so an await in startup or component code has to come back here.
			// A host that already installed a context keeps its own.
			if (SynchronizationContext.Current is null)
			{
				SynchronizationContext.SetSynchronizationContext(_queue.SynchronizationContext);
			}

			Window = window;
			_compositionHost = new CompositionWebViewHost(window);
			RootComponents.CollectionChanged += HandleRootComponentsChanged;
		}

		/// <summary>The window this view renders into.</summary>
		public Win32Window Window { get; }

		/// <summary>The Blazor dispatcher that runs work on this view's UI thread.</summary>
		public Dispatcher Dispatcher => _dispatcher;

		/// <summary>
		/// Path of the host page inside the application's static content, e.g. <c>wwwroot/index.html</c>.
		/// Must be set before <see cref="StartAsync"/>.
		/// </summary>
		public string? HostPage
		{
			get => _hostPage;
			set
			{
				ThrowIfStarted();
				_hostPage = value;
			}
		}

		/// <summary>Path the app navigates to once Blazor has started. Defaults to <c>/</c>.</summary>
		public string StartPath
		{
			get => _startPath;
			set
			{
				ArgumentNullException.ThrowIfNull(value);
				ThrowIfStarted();
				_startPath = value;
			}
		}

		/// <summary>The components mounted in the host page. Changeable before and after start.</summary>
		public RootComponentsCollection RootComponents { get; } = new();

		/// <summary>Services for this view and its components. Must be set before <see cref="StartAsync"/>.</summary>
		public IServiceProvider? Services
		{
			get => _services;
			set
			{
				ThrowIfStarted();
				_services = value;
			}
		}

		/// <summary>
		/// Assembly whose embedded resources carry the site's static content, or <see langword="null"/> to look at the entry
		/// assembly — which is what the build embeds into by default. Set it when a Razor class library carries the content.
		/// </summary>
		/// <remarks>
		/// Content found here is served ahead of the files next to the executable, so an app published without a content
		/// folder still renders, and a stale folder left in an output directory cannot shadow the bytes that shipped inside
		/// the exe. An assembly with no embedded content changes nothing: such an app keeps reading from disk.
		/// See <see cref="CreateFileProvider"/>.
		/// </remarks>
		public Assembly? EmbeddedContentAssembly { get; set; }

		/// <summary>
		/// Resource folder the content is embedded under, matched to the build's <c>Win32BlazorWebViewAotEmbeddedBaseFolder</c>.
		/// Defaults to <c>wwwroot</c>.
		/// </summary>
		public string EmbeddedContentBaseFolder { get; set; } = EmbeddedContent.DefaultBaseFolder;

		/// <summary>The browser controller, or <see langword="null"/> before start and after disposal.</summary>
		/// <remarks>Do not close or dispose it: the view owns its lifetime.</remarks>
		public IComObject<ICoreWebView2Controller>? Controller => _compositionHost.Controller;

		/// <summary>The browser, or <see langword="null"/> before start and after disposal.</summary>
		/// <remarks>Do not dispose it: the view owns its lifetime.</remarks>
		public IComObject<ICoreWebView2>? WebView => _compositionHost.WebView;

		/// <summary>The composition controller this view renders through, or <see langword="null"/> before start.</summary>
		public IComObject<ICoreWebView2CompositionController>? CompositionController => _compositionHost.CompositionController;

		/// <summary>
		/// Whether drags started outside the window (from Explorer or another application) reach the page. On by default; a
		/// composition-hosted browser has no window of its own, so the host owns the drop target and forwards to it.
		/// </summary>
		public bool AllowDrop
		{
			get => _compositionHost.AllowDrop;
			set => _compositionHost.AllowDrop = value;
		}

		/// <summary>Whether the browser is shown. A minimized window should not keep rendering.</summary>
		public bool IsVisible
		{
			get => _isVisible;
			set
			{
				_isVisible = value;
				_compositionHost.SetVisible(value);
			}
		}

		private bool _isVisible = true;

		/// <summary><see langword="true"/> once disposal has started.</summary>
		public bool IsDisposed => _disposeTask is not null;

		/// <summary>Decides where a link opens. By default the app's own origin stays and everything else leaves.</summary>
		public event EventHandler<UrlLoadingEventArgs>? UrlLoading;

		/// <summary>Configures WebView2 before the browser is created (user data folder, options).</summary>
		public event EventHandler<BlazorWebViewInitializingEventArgs>? BlazorWebViewInitializing;

		/// <summary>Hands over the browser objects after creation, before the first navigation.</summary>
		public event EventHandler<BlazorWebViewInitializedEventArgs>? BlazorWebViewInitialized;

		/// <summary>
		/// Raised on the UI thread when work Blazor dispatched threw. Set
		/// <see cref="BlazorWebViewUnhandledExceptionEventArgs.Handled"/> to keep the process alive.
		/// </summary>
		public event EventHandler<BlazorWebViewUnhandledExceptionEventArgs>? UnhandledException;

		/// <summary>
		/// Raised when a WebView2 browser process dies. Subscribing is how an app survives one: without a handler a terminal
		/// failure (browser or renderer exit) is promoted to an unhandled exception, because a window that can no longer
		/// render is not a window worth keeping open silently.
		/// </summary>
		public event EventHandler<BrowserProcessFailedEventArgs>? BrowserProcessFailed;

		/// <summary>
		/// Starts the browser and Blazor. Completes once the host page navigation has been issued. Calling it again returns
		/// the same task.
		/// </summary>
		/// <exception cref="InvalidOperationException"><see cref="HostPage"/> or <see cref="Services"/> is not set.</exception>
		/// <exception cref="Exception">WebView2 could not start, e.g. no runtime installed, or the host page is missing.</exception>
		public Task StartAsync(CancellationToken cancellationToken = default)
		{
			ObjectDisposedException.ThrowIf(IsDisposed, this);
			ThrowIfNotOnUIThread();

			return _startTask ??= StartCoreAsync(cancellationToken);
		}

		private async Task StartCoreAsync(CancellationToken cancellationToken)
		{
			if (HostPage is null)
			{
				throw new InvalidOperationException($"'{nameof(HostPage)}' must be set before calling '{nameof(StartAsync)}'.");
			}

			if (Services is null)
			{
				throw new InvalidOperationException($"'{nameof(Services)}' must be set before calling '{nameof(StartAsync)}'.");
			}

			_logger = Services.GetService<ILogger<BlazorWebView>>() ?? NullLogger<BlazorWebView>.Instance;

			// Asked for before resolving the host page: a page that exists only inside the executable resolves because this
			// answers, so the order matters and the provider is built once for both uses.
			var embeddedContent = EmbeddedContent.Create(EmbeddedContentAssembly, EmbeddedContentBaseFolder);

			// The host page always sits at the root of its content directory; there is no known case where it does not.
			var (appRootDir, hostPageFullPath) = HostPageLocator.Resolve(HostPage, AppContext.BaseDirectory, embeddedContent);
			var contentRootDir = Path.GetDirectoryName(hostPageFullPath)!;
			var contentRootRelativePath = Path.GetRelativePath(appRootDir, contentRootDir);
			var hostPageRelativePath = Path.GetRelativePath(contentRootDir, hostPageFullPath);

			_logger.CreatingFileProvider(contentRootDir, hostPageRelativePath);

			var manager = new WebView2AotBlazorManager(
				_compositionHost,
				Services,
				_dispatcher,
				CreateFileProvider(contentRootDir),
				RootComponents.JSComponents,
				contentRootRelativePath,
				hostPageRelativePath,
				args => UrlLoading?.Invoke(this, args),
				args => BlazorWebViewInitializing?.Invoke(this, args),
				args => BlazorWebViewInitialized?.Invoke(this, args),
				_logger);

			_manager = manager;
			manager.BrowserProcessFailed += HandleBrowserProcessFailed;

			// The browser has to exist before anything can navigate: a navigation issued to a window with no browser is
			// dropped rather than queued, and the app would open on a blank page.
			await manager.InitializeAsync(cancellationToken).ConfigureAwait(true);

			foreach (var rootComponent in RootComponents)
			{
				_logger.AddingRootComponent(
					rootComponent.ComponentType.FullName ?? string.Empty,
					rootComponent.Selector,
					rootComponent.Parameters?.Count ?? 0);

				// The page is not loaded yet, so this completes synchronously.
				_ = rootComponent.AddToWebViewManagerAsync(manager);
			}

			_logger.StartingInitialNavigation(StartPath);
			manager.Navigate(StartPath);
		}

		/// <summary>
		/// Creates the provider that serves static assets: content embedded in the assembly first, then the files under
		/// <paramref name="contentRootDir"/>. Override to serve content from elsewhere; combine with a
		/// <see cref="CompositeFileProvider"/> to keep either source.
		/// </summary>
		/// <param name="contentRootDir">Directory all asset paths are relative to, typically the host page's folder.</param>
		public virtual IFileProvider CreateFileProvider(string contentRootDir)
		{
			var embedded = EmbeddedContent.Create(EmbeddedContentAssembly, EmbeddedContentBaseFolder);

			// In development the content is not copied next to the executable; Microsoft's static-content provider serves it.
			IFileProvider disk = Directory.Exists(contentRootDir) ? new PhysicalFileProvider(contentRootDir) : new NullFileProvider();

			return embedded switch
			{
				null => disk,
				_ when disk is NullFileProvider => embedded,
				_ => new CompositeFileProvider(embedded, disk),
			};
		}

		/// <summary>
		/// Runs <paramref name="workItem"/> with the scoped services a component would see.
		/// </summary>
		/// <returns><see langword="false"/> when Blazor is not running, so the work item was never called.</returns>
		public virtual async Task<bool> TryDispatchAsync(Action<IServiceProvider> workItem)
		{
			ArgumentNullException.ThrowIfNull(workItem);

			var manager = _manager;
			return manager is not null && !IsDisposed && await manager.TryDispatchAsync(workItem).ConfigureAwait(false);
		}

		/// <summary>Tells the browser its window moved so anchored popups follow it.</summary>
		public void NotifyParentWindowPositionChanged() => _compositionHost.NotifyParentWindowPositionChanged();

		/// <summary>Moves keyboard focus into the browser.</summary>
		/// <returns><see langword="true"/> when the browser existed and took the focus.</returns>
		public bool MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON reason = COREWEBVIEW2_MOVE_FOCUS_REASON.COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC) =>
			Controller is { } controller && controller.MoveFocus(reason, throwOnError: false).IsSuccess;

		/// <summary>
		/// Disconnects components and the browser. Must run on the UI thread; later calls return the same task.
		/// </summary>
		/// <remarks>
		/// Await this before destroying the window: component disposal needs a live browser, and the message loop has to
		/// keep pumping while it runs.
		/// </remarks>
		public ValueTask DisposeAsync()
		{
			if (_disposeTask is null)
			{
				ThrowIfNotOnUIThread();
				_disposeTask = DisposeCoreAsync();
			}

			return new ValueTask(_disposeTask);
		}

		/// <summary>Tears the view down. Override to add teardown, and call the base implementation.</summary>
		protected virtual async Task DisposeCoreAsync()
		{
			RootComponents.CollectionChanged -= HandleRootComponentsChanged;

			try
			{
				var manager = _manager;
				if (manager is not null)
				{
					_manager = null;

					try
					{
						await DisconnectIpcAsync().ConfigureAwait(true);
					}
					catch (Exception exception)
					{
						// Best effort: the browser process may already be gone.
						_logger.LogDebug(exception, "Failed to disconnect the WebView2 IPC bridge.");
					}

					// Components first: their disposal code can no longer run once the browser is closed.
					await _dispatcher.InvokeAsync(manager.DisposeAsync).ConfigureAwait(true);
				}
			}
			finally
			{
				// Then the browser and its visual, then the environment that created them. The queue and its
				// synchronization context deliberately stay alive: whoever is awaiting this disposal still needs them, and
				// other views on the thread share them.
				_compositionHost.Dispose();
				_manager?.ReleaseOwnedEnvironment();

				_queue.UnhandledException -= HandleQueueUnhandledException;
			}
		}

		/// <summary>
		/// Silences the JS side of the IPC bridge before teardown, so a late message cannot reach a disposed renderer
		/// (dotnet/maui#34855).
		/// </summary>
		protected virtual async Task DisconnectIpcAsync()
		{
			if (WebView is { } webView)
			{
				await webView.ExecuteScriptAsync(
					"""
					window.external = {
						sendMessage: message => {},
						receiveMessage: callback => {}
					};
					""").ConfigureAwait(true);
			}
		}

		private void HandleRootComponentsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
		{
			// Before start the collection is applied by StartCoreAsync; after disposal there is nothing to apply it to.
			if (_manager is not { } manager || IsDisposed)
			{
				return;
			}

			_ = _dispatcher.InvokeAsync(async () =>
			{
				var added = (eventArgs.NewItems ?? Array.Empty<object>()).Cast<RootComponent>();
				var removed = (eventArgs.OldItems ?? Array.Empty<object>()).Cast<RootComponent>();

				foreach (var component in added.Except(removed))
				{
					await component.AddToWebViewManagerAsync(manager).ConfigureAwait(true);
				}

				foreach (var component in removed.Except(added))
				{
					await component.RemoveFromWebViewManagerAsync(manager).ConfigureAwait(true);
				}
			});
		}

		private void HandleBrowserProcessFailed(BrowserProcessFailedEventArgs args)
		{
			BrowserProcessFailed?.Invoke(this, args);

			if (args.IsTerminal && BrowserProcessFailed is null)
			{
				HandleDispatchedFailure(new InvalidOperationException($"The WebView2 {args} and the page can no longer render."));
			}
		}

		private void HandleDispatchedFailure(Exception exception)
		{
			if (IsDisposed)
			{
				// Nothing left to promote it to. The caller's task still faults.
				_logger.LogDebug(exception, "Ignoring an exception from work dispatched after the view was disposed.");
				return;
			}

			var args = new BlazorWebViewUnhandledExceptionEventArgs(exception);
			try
			{
				RaiseUnhandledException(args);
			}
			catch (Exception handlerException)
			{
				Win32MessageQueue.RethrowAsUnhandled(new AggregateException(exception, handlerException));
				return;
			}

			if (!args.Handled)
			{
				Win32MessageQueue.RethrowAsUnhandled(exception);
			}
		}

		private void HandleQueueUnhandledException(object? sender, Win32UnhandledExceptionEventArgs eventArgs)
		{
			if (eventArgs.Handled)
			{
				return;
			}

			// Work posted through the synchronization context (async void continuations) reaches the same handler a
			// component exception does, so an app has one place to decide whether to survive.
			var args = new BlazorWebViewUnhandledExceptionEventArgs(eventArgs.Exception);
			RaiseUnhandledException(args);
			eventArgs.Handled = args.Handled;
		}

		private void RaiseUnhandledException(BlazorWebViewUnhandledExceptionEventArgs args)
		{
			_logger.LogError(args.Exception, "Unhandled exception in work dispatched by {view}.", nameof(BlazorWebView));
			UnhandledException?.Invoke(this, args);
		}

		private void ThrowIfStarted()
		{
			if (_startTask is not null)
			{
				throw new InvalidOperationException($"This property cannot be changed after the view has started.");
			}
		}

		private void ThrowIfNotOnUIThread()
		{
			if (!_queue.CheckAccess())
			{
				throw new InvalidOperationException("A BlazorWebView must be used on the thread that created it.");
			}
		}
	}
}
