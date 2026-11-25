// The WebView2 half of a window: an ICoreWebView2CompositionController wired into the visual tree, plus the input and
// cursor plumbing only composition hosting needs. Knows nothing about Blazor.
// Why it stays separate from WebView2AotBlazorManager: "Layers" in doc/DESIGN.md.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Hosts a WebView2 browser as a visual in a window's composition tree and forwards native input to it.
	/// </summary>
	public sealed class CompositionWebViewHost : IDisposable
	{
		private readonly Win32Window _window;
		private IComObject<ICoreWebView2CompositionController>? _compositionController;
		private IComObject<ICoreWebView2Controller>? _controller;
		private IComObject<ICoreWebView2>? _webView;
		private CompositionControllerEvents? _controllerEvents;
		private CompositionPointerHandler? _pointerHandler;
		private IComObject<IOleInPlaceActiveObject>? _inPlaceActiveObject;
		private CompositionDropTarget? _dropTarget;
		private bool _isDisposed;

		/// <summary>Creates the composition surface for <paramref name="window"/> and attaches it to its message flow.</summary>
		public CompositionWebViewHost(Win32Window window)
		{
			ArgumentNullException.ThrowIfNull(window);

			_window = window;
			Target = new CompositionTarget(window.Handle);
			window.AttachComposition(this);
			Target.Resize(window.ClientRect);

			// A composition-hosted page can only receive a drop because this host registered for it, so it is on by default.
			AllowDrop = true;
		}

		/// <summary>The composition tree this host renders into.</summary>
		public CompositionTarget Target { get; }

		/// <summary>
		/// Whether the window accepts drags started outside it. Setting it registers or revokes the OLE drop
		/// target, so it can be changed at any time; drops that arrive before the browser exists are refused.
		/// </summary>
		/// <exception cref="InvalidOperationException">OLE could not be initialized on this thread (it must be STA).</exception>
		public bool AllowDrop
		{
			get => _dropTarget is not null;
			set
			{
				ObjectDisposedException.ThrowIf(_isDisposed, this);

				if (value == (_dropTarget is not null))
				{
					return;
				}

				if (value)
				{
					OleApartment.AddRef();

					var target = new CompositionDropTarget(_window.Handle, () => _compositionController);
					var result = Native.RegisterDragDrop(_window.Handle, target);

					// Already registered means a previous host on this HWND never revoked — take it over rather than fail.
					if (result.IsError && result.Value != DragdropAlreadyRegistered)
					{
						OleApartment.Release();
						throw new InvalidOperationException(
							$"RegisterDragDrop failed with 0x{result.Value:x8}; drag & drop is unavailable on this window.");
					}

					_dropTarget = target;
					return;
				}

				ReleaseDropTarget();
			}
		}

		/// <summary>The composition controller, or <see langword="null"/> before the browser is created.</summary>
		internal IComObject<ICoreWebView2CompositionController>? CompositionController => _compositionController;

		/// <summary>
		/// The same browser as its common base interface, for code that expects an ordinary controller (bounds, focus).
		/// </summary>
		internal IComObject<ICoreWebView2Controller>? Controller => _controller;

		/// <summary>The browser instance, or <see langword="null"/> before initialization completes.</summary>
		internal IComObject<ICoreWebView2>? WebView => _webView;

		/// <summary>
		/// Creates the composition controller for <paramref name="environment"/>, attaches it to the root visual and
		/// stretches it across the client area. Must run on the window's UI thread.
		/// </summary>
		internal async Task InitializeAsync(IComObject<ICoreWebView2Environment> environment)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);
			ArgumentNullException.ThrowIfNull(environment);

			if (_compositionController is not null)
			{
				throw new InvalidOperationException("The composition controller has already been created.");
			}

			var compositionController = await environment.CreateCoreWebView2CompositionControllerAsync(_window.Handle).ConfigureAwait(true)
				?? throw new InvalidOperationException(
					$"WebView2 returned no composition controller. Runtime: {WebView2Functions.GetAvailableCoreWebView2BrowserVersionStringWithOptions(null, null) ?? "not installed"}");

			try
			{
				_compositionController = compositionController;

				// A second wrapper over the same browser object: it holds its own reference and is disposed on its own,
				// which is why this is not double disposal.
				var baseController = compositionController.As<ICoreWebView2Controller>()!;
				_controller = baseController;
				_webView = baseController.CoreWebView2 ?? throw new InvalidOperationException("The WebView2 controller has no browser.");

				_controllerEvents = new CompositionControllerEvents(compositionController);
				_controllerEvents.CursorChanged += HandleCursorChanged;

				_pointerHandler = new CompositionPointerHandler(compositionController);

				// The browser has no window of its own, so keyboard messages reach it only through this interface.
				_inPlaceActiveObject = compositionController.As<IOleInPlaceActiveObject>();

				// Attach the visual before navigating: a browser with no render target paints nothing and never recovers.
				compositionController.RootVisualTarget = Target.RootVisual;

				var clientRect = _window.ClientRect;
				baseController.Bounds = clientRect;
				baseController.IsVisible = true;
				Target.Resize(clientRect);
			}
			catch
			{
				ReleaseBrowser();
				throw;
			}
		}

		/// <summary>Keeps the visual and the browser bounds in step with the client area.</summary>
		internal void Resize(RECT clientRect)
		{
			if (_isDisposed)
			{
				return;
			}

			Target.Resize(clientRect);

			if (_controller is { } controller)
			{
				// Setting bounds on a browser that was just closed throws the invalid-state HRESULT; layout is advisory.
				try
				{
					controller.Bounds = clientRect;
				}
				catch (Exception)
				{
					// Ignored: the browser is going away.
				}
			}
		}

		/// <summary>Hides or shows the browser (a minimized window should not keep rendering).</summary>
		internal void SetVisible(bool isVisible)
		{
			if (_controller is { } controller)
			{
				try
				{
					controller.IsVisible = isVisible;
				}
				catch (Exception)
				{
					// Ignored: the browser is going away.
				}
			}
		}

		/// <summary>Moves keyboard focus into the browser.</summary>
		internal void MoveFocus() => _ = _controller?.MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON.COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC, throwOnError: false);

		/// <summary>Tells the browser its parent window moved, so dropdowns and popups anchor correctly.</summary>
		internal void NotifyParentWindowPositionChanged() => _ = _controller?.NotifyParentWindowPositionChanged(throwOnError: false);

		/// <summary>Forwards a client-area pointer move.</summary>
		internal void SendMouseMove(uint keyStates, POINT clientPoint) =>
			SendMouse(CompositionInputHandler.MoveKind, keyStates, 0, clientPoint);

		/// <summary>Forwards a client-area mouse button message.</summary>
		internal void SendMouseButton(uint message, uint keyStates, POINT clientPoint)
		{
			if (CompositionInputHandler.ButtonKind(message) is not { } kind)
			{
				return;
			}

			// Double-clicks arrive as down/up/dblclk; the browser needs all three to select words and lines.
			SendMouse(kind, keyStates, 0, clientPoint);
		}

		/// <summary>
		/// Forwards a wheel message. Wheel messages carry screen coordinates (unlike every other mouse message), so the
		/// caller converts them before arriving here.
		/// </summary>
		internal void SendMouseWheel(bool isHorizontal, int delta, uint keyStates, POINT clientPoint) =>
			SendMouse(CompositionInputHandler.WheelKind(isHorizontal), keyStates, unchecked((uint)delta), clientPoint);

		/// <summary>Tells the browser the pointer left the window, so hover states can clear.</summary>
		internal void SendMouseLeave() => SendMouse(CompositionInputHandler.LeaveKind, 0, 0, default);

		/// <summary>
		/// Forwards one <c>WM_POINTER*</c> message (touch or pen). Does nothing when the installed runtime cannot describe a
		/// pointer; mouse messages keep working in that case.
		/// </summary>
		internal void SendPointer(uint message, uint pointerId) => _pointerHandler?.Send(message, pointerId, _window.Handle);

		/// <summary>
		/// Gives the browser first refusal on a keyboard message before the loop translates it. This is how tab traversal,
		/// browser accelerators and IME-driven text composition arrive at a composition-hosted page.
		/// </summary>
		/// <param name="messageAddress">Address of the <c>MSG</c> being pumped; WebView2 reads it, it is not copied.</param>
		/// <returns><see langword="true"/> when the browser consumed the message and the loop must not translate it.</returns>
		internal bool TryTranslateAccelerator(nint messageAddress)
		{
			if (_isDisposed || _inPlaceActiveObject is null)
			{
				return false;
			}

			// S_OK means handled; anything else (including a browser that just died) means carry on with the normal loop.
			return _inPlaceActiveObject.Object.TranslateAccelerator(messageAddress).IsSuccess;
		}

		private void SendMouse(COREWEBVIEW2_MOUSE_EVENT_KIND kind, uint keyStates, uint mouseData, POINT clientPoint)
		{
			if (_isDisposed || _compositionController is null)
			{
				return;
			}

			// The browser can disappear under us (crash, teardown); input is best-effort by nature.
			_ = _compositionController.Object.SendMouseInput(
				kind,
				CompositionInputHandler.TranslateKeyStates(keyStates),
				mouseData,
				clientPoint);
		}

		// OLE drag-registration failures are not HRESULTs in the system range, so DirectN exposes no fields for them.
		private const int DragdropAlreadyRegistered = unchecked((int)0x80040100);
		private const int DragdropNotRegistered = unchecked((int)0x80040001);

		/// <summary>
		/// Unregisters the drop target and drops this host's reference on the thread's OLE apartment. Revoking is best-effort:
		/// a window being torn down may already have lost its registration, which is the case the error reports.
		/// </summary>
		private void ReleaseDropTarget()
		{
			if (Interlocked.Exchange(ref _dropTarget, null) is null)
			{
				return;
			}

			var result = Native.RevokeDragDrop(_window.Handle);
			System.Diagnostics.Debug.Assert(
				result.IsSuccess || result.Value == DragdropNotRegistered,
				$"RevokeDragDrop failed with 0x{result.Value:x8}");

			OleApartment.Release();
		}

		private void HandleCursorChanged(object? sender, HCURSOR cursor)
		{
			if (_isDisposed)
			{
				return;
			}

			// WM_SETCURSOR applies it on the next mouse move; applying it now also covers a cursor change while the
			// pointer is stationary (a page restyling the element under the pointer).
			_window.SetWebViewCursor(cursor);
			_ = Native.SetCursor(cursor);
		}

		/// <inheritdoc />
		public void Dispose()
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;

			// Revoke before closing the browser: a drag still in flight must not be forwarded to a controller that is gone.
			ReleaseDropTarget();
			ReleaseBrowser();
			Target.Dispose();
		}

		private void ReleaseBrowser()
		{
			// Order matters: unsubscribe, then detach the visual, then close the controller, then drop the wrappers.
			Interlocked.Exchange(ref _controllerEvents, null)?.Dispose();
			Interlocked.Exchange(ref _pointerHandler, null);
			Interlocked.Exchange(ref _inPlaceActiveObject, null)?.Dispose();

			var compositionController = Interlocked.Exchange(ref _compositionController, null);
			if (compositionController is not null)
			{
				try
				{
					compositionController.RootVisualTarget = null;
				}
				catch (Exception)
				{
					// The browser process may already be gone.
				}
			}

			var controller = Interlocked.Exchange(ref _controller, null);
			controller?.Close(throwOnError: false);

			Interlocked.Exchange(ref _webView, null)?.Dispose();
			controller?.Dispose();
			compositionController?.Dispose();
		}
	}
}
