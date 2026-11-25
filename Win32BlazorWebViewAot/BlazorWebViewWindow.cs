// A window that owns a BlazorWebView and drives it from its own messages. Close is deferred: the view is disposed on the
// message queue first, and the HWND is destroyed only afterwards.
// Why the deferral is required: "Lifetime" in doc/DESIGN.md.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// A Win32 window with the normal Windows title bar, whose client area is a Blazor application.
	/// </summary>
	/// <remarks>
	/// To give the page the whole window instead — no OS caption, so the HTML draws its own bar — use
	/// <see cref="BorderlessBlazorWebViewWindow"/>.
	/// </remarks>
	/// <remarks>
	/// <example>
	/// <code>
	/// using var window = new BlazorWebViewWindow("My App")
	/// {
	///     Services = services,
	///     HostPage = "wwwroot/index.html",
	/// };
	/// window.RootComponents.Add&lt;App&gt;("#app");
	/// window.Show();
	/// await window.StartAsync();
	/// window.Run();
	/// </code>
	/// </example>
	/// <para>
	/// <see cref="Win32Window.Close"/> and the close button both run that teardown. Dispose without a prior
	/// <see cref="BlazorWebView.DisposeAsync"/> is supported but skips component disposal.
	/// </para>
	/// </remarks>
	public class BlazorWebViewWindow : Win32Window
	{
		private Task? _closeTask;

		/// <summary>Creates a window and the view that fills it.</summary>
		/// <param name="title">Caption text (taskbar and Alt+Tab).</param>
		/// <param name="rect">Initial size in physical pixels, or <see langword="null"/> for the system default.</param>
		public BlazorWebViewWindow(string? title = null, RECT? rect = null)
			: this(title, WindowStyles.TitleBar, WindowStyles.Composition, rect)
		{
		}

		/// <summary>
		/// Creates a window with an explicit frame, for subclasses that sell a different chrome under their own name —
		/// see <see cref="BorderlessBlazorWebViewWindow"/>. Chrome cannot change after construction: styles are fixed at
		/// <c>CreateWindowExW</c> time.
		/// </summary>
		/// <param name="title">Caption text (taskbar and Alt+Tab).</param>
		/// <param name="style">Frame styles, e.g. <see cref="WindowStyles.TitleBar"/> or <see cref="WindowStyles.Borderless"/>.</param>
		/// <param name="extendedStyle">Extended styles; see <see cref="WindowStyles.Composition"/>.</param>
		/// <param name="rect">Initial size in physical pixels, or <see langword="null"/> for the system default.</param>
		protected BlazorWebViewWindow(string? title, WINDOW_STYLE style, WINDOW_EX_STYLE extendedStyle, RECT? rect)
			: base(title, style, extendedStyle, rect)
		{
			View = new BlazorWebView(this);
			View.BlazorWebViewInitialized += HandleInitialized;
		}

		/// <summary>The view this window hosts.</summary>
		public BlazorWebView View { get; }

		/// <summary>
		/// The view this window hosts, under the name keeps for compatibility with existing consumers
		/// (<c>window.BlazorWebView.HostPage = ...</c>). Same object as <see cref="View"/>.
		/// </summary>
		public BlazorWebView BlazorWebView => View;

		/// <inheritdoc cref="BlazorWebView.Services"/>
		public IServiceProvider? Services
		{
			get => View.Services;
			set => View.Services = value;
		}

		/// <inheritdoc cref="BlazorWebView.HostPage"/>
		public string? HostPage
		{
			get => View.HostPage;
			set => View.HostPage = value;
		}

		/// <inheritdoc cref="BlazorWebView.StartPath"/>
		public string StartPath
		{
			get => View.StartPath;
			set => View.StartPath = value;
		}

		/// <summary>The components mounted in the host page.</summary>
		public RootComponentsCollection RootComponents => View.RootComponents;

		/// <summary>
		/// Whether external drags (from Explorer or another application) reach the page. On by default.
		/// </summary>
		public bool AllowDrop
		{
			get => View.AllowDrop;
			set => View.AllowDrop = value;
		}

		/// <inheritdoc cref="BlazorWebView.UnhandledException"/>
		public event EventHandler<BlazorWebViewUnhandledExceptionEventArgs>? UnhandledException
		{
			add => View.UnhandledException += value;
			remove => View.UnhandledException -= value;
		}

		/// <inheritdoc cref="BlazorWebView.BrowserProcessFailed"/>
		public event EventHandler<BrowserProcessFailedEventArgs>? BrowserProcessFailed
		{
			add => View.BrowserProcessFailed += value;
			remove => View.BrowserProcessFailed -= value;
		}

		/// <inheritdoc cref="BlazorWebView.UrlLoading"/>
		public event EventHandler<UrlLoadingEventArgs>? UrlLoading
		{
			add => View.UrlLoading += value;
			remove => View.UrlLoading -= value;
		}

		/// <inheritdoc cref="BlazorWebView.BlazorWebViewInitializing"/>
		public event EventHandler<BlazorWebViewInitializingEventArgs>? BlazorWebViewInitializing
		{
			add => View.BlazorWebViewInitializing += value;
			remove => View.BlazorWebViewInitializing -= value;
		}

		/// <inheritdoc cref="BlazorWebView.BlazorWebViewInitialized"/>
		public event EventHandler<BlazorWebViewInitializedEventArgs>? BlazorWebViewInitialized
		{
			add => View.BlazorWebViewInitialized += value;
			remove => View.BlazorWebViewInitialized -= value;
		}

		/// <summary>Starts Blazor in this window. See <see cref="BlazorWebView.StartAsync"/>.</summary>
		public Task StartAsync(CancellationToken cancellationToken = default) => View.StartAsync(cancellationToken);

		/// <inheritdoc />
		protected override void OnActivated(bool isActive)
		{
			if (isActive)
			{
				View.MoveFocus();
			}
		}

		/// <summary>
		/// Defers destruction until the view finished disposing asynchronously, which is what lets a component's
		/// asynchronous disposal logic run against a live browser.
		/// </summary>
		protected override bool OnCloseRequested()
		{
			_closeTask ??= CloseCoreAsync();
			return true;
		}

		private async Task CloseCoreAsync()
		{
			try
			{
				await View.DisposeAsync().ConfigureAwait(true);
			}
			catch (Exception)
			{
				// A window that fails to close cleanly still has to close; the exception is not what the user is waiting on.
			}
			finally
			{
				// Back on the UI thread through the queue's synchronization context. Destroying the HWND raises WM_DESTROY,
				// which posts WM_QUIT and ends Run().
				DestroyWindow();
			}
		}

		private void HandleInitialized(object? sender, BlazorWebViewInitializedEventArgs eventArgs)
		{
			// The composition visual is stretched by the window; nothing to place, but the window has to be told the
			// browser exists so a later minimize can hide it.
			View.IsVisible = IsVisible;
		}

		/// <inheritdoc />
		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				View.BlazorWebViewInitialized -= HandleInitialized;

				// Disposal of the view is normally done by CloseCoreAsync. Reaching here without it means the owner threw
				// the window away synchronously: release the browser, but do not await on a message loop that may be gone.
				if (!View.IsDisposed)
				{
					_ = View.DisposeAsync().AsTask();
				}
			}

			base.Dispose(disposing);
		}
	}
}
