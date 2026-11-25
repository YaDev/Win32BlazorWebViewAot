// The Windows.UI.Composition tree that a borderless window renders through.
//
// Responsibilities: own the compositor controller, create the desktop target for the HWND, own the root visual the
// WebView draws into, size it with the client area, commit changes, and release the tree on teardown.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Owns the composition surface of a window: compositor controller, desktop target and root visual.
	/// </summary>
	/// <remarks>
	/// This is our type; the WinRT class it wraps is <c>global::Windows.UI.Composition.CompositionTarget</c>, which is
	/// why that one appears fully qualified below.
	/// </remarks>
	public sealed class CompositionTarget : IDisposable
	{
		private readonly CompositorController _controller;
		private readonly TypedEventHandler<CompositorController, object> _commitNeeded;
		private readonly global::Windows.UI.Composition.CompositionTarget _desktopTarget;
		private bool _isDisposed;

		/// <summary>
		/// Creates the composition tree for <paramref name="hwnd"/>. Must run on the window's UI thread.
		/// </summary>
		/// <param name="hwnd">The window whose pixels will come from this tree.</param>
		/// <param name="isTopMost">
		/// Passed to <c>CreateDesktopWindowTarget</c>: <see langword="true"/> composes the window above sibling
		/// redirection surfaces, which is what a window with no redirection bitmap needs to be visible at all.
		/// </param>
		public CompositionTarget(HWND hwnd, bool isTopMost = true)
		{
			if (hwnd.Value == 0)
			{
				throw new ArgumentException("A valid window handle is required.", nameof(hwnd));
			}

			// CompositorController is a Windows.UI.Composition.Core type and refuses to activate (E_ACCESSDENIED with
			// "the caller must initialize DispatcherQueue on this thread") unless the thread hosts a Windows.System
			// DispatcherQueue. A plain Win32 message loop is not one, so we create it here — DQTYPE_THREAD_CURRENT ties
			// it to this thread and it dies with the thread, which matches the window owning the loop. One controller per UI thread, created once.
			EnsureDispatcherQueue();

			_controller = new CompositorController();

			// The compositor batches visual changes; without a commit nothing ever reaches the screen. CommitNeeded is
			// the pull model Windows.UI.Composition offers for hosts that are not driven by a XAML framework.
			_commitNeeded = static (sender, _) => sender?.Commit();
			_controller.CommitNeeded += _commitNeeded;

			var desktopInterop = _controller.Compositor.As<ICompositorDesktopInterop>();
			desktopInterop.CreateDesktopWindowTarget(hwnd, isTopMost, out var targetAbi).ThrowOnError();

			try
			{
				_desktopTarget = MarshalInspectable<global::Windows.UI.Composition.CompositionTarget>.FromAbi(targetAbi);
				RootVisual = Compositor.CreateSpriteVisual();
				_desktopTarget.Root = RootVisual;
			}
			catch
			{
				_controller.Dispose();
				throw;
			}
		}

		[ThreadStatic]
		private static WindowsDispatcherQueueController? _dispatcherQueue;

		private static void EnsureDispatcherQueue()
		{
			var controller = _dispatcherQueue ??= new WindowsDispatcherQueueController();
			controller.EnsureOnCurrentThread();
		}

		/// <summary>The compositor that creates visuals for this tree.</summary>
		public Compositor Compositor => _controller.Compositor;

		/// <summary>The visual the window occupies; the WebView is attached to it as its render target.</summary>
		public SpriteVisual RootVisual { get; }

		/// <summary>Sizes the root visual to a client rectangle and commits the change.</summary>
		public void Resize(RECT clientRect)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			RootVisual.Size = new System.Numerics.Vector2(clientRect.Width, clientRect.Height);
			Commit();
		}

		/// <summary>Flushes queued visual changes to the compositor.</summary>
		public void Commit() => _controller.Commit();

		/// <inheritdoc />
		public void Dispose()
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;

			// Detach before releasing: a desktop target still pointing at a freed visual keeps the tree alive.
			try
			{
				_desktopTarget.Root = null;
			}
			catch (Exception)
			{
				// The compositor may already be gone with the window; nothing left to detach.
			}

			RootVisual.Dispose();
			_controller.CommitNeeded -= _commitNeeded;
			_controller.Dispose();
		}
	}
}
