// A top-level native window: HWND, message pump, borderless frame, DPI, resize hit-testing and the input hooks a
// composition-hosted WebView needs. It knows nothing about Blazor.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// A native Win32 top-level window with its own message loop, independent of any UI framework.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The window is created on the calling thread, which must be an STA thread; WebView2 and the composition tree are
	/// both thread-affine. Create, use and dispose a window on that single thread.
	/// </para>
	/// <para>
	/// Subclasses hook individual messages by overriding the <c>On*</c> methods. <see cref="BlazorWebViewWindow"/> uses
	/// them to drive a <see cref="BlazorWebView"/>; <see cref="CompositionWebViewHost"/> is attached through
	/// <see cref="AttachComposition"/>.
	/// </para>
	/// </remarks>
	public class Win32Window : IDisposable
	{
		private readonly WINDOW_STYLE _style;
		private readonly WINDOW_EX_STYLE _extendedStyle;
		private readonly RECT? _initialRect;
		private string _title = string.Empty;
		private CompositionWebViewHost? _composition;
		private HCURSOR _webViewCursor;
		private bool _isMouseTracking;
		private bool _hasMouseCapture;
		private bool _closeRequested;
		private bool _isDisposed;

		/// <summary>Creates a window. Nothing is sent to the message loop until <see cref="Show"/> or <see cref="Run"/>.</summary>
		/// <param name="title">The caption (taskbar text and Alt+Tab label).</param>
		/// <param name="style">
		/// Window styles; defaults to <see cref="WindowStyles.TitleBar"/>, so Windows draws a normal title bar. Pass
		/// <see cref="WindowStyles.Borderless"/> (or use <see cref="BorderlessWin32Window"/>) to give the page the whole
		/// window. The chrome-dependent message handling follows this value — see <see cref="Chrome"/>.
		/// </param>
		/// <param name="extendedStyle">Extended styles; defaults to <c>WS_EX_NOREDIRECTIONBITMAP</c>.</param>
		/// <param name="rect">Initial window rectangle in physical pixels, or <see langword="null"/> for the system default position.</param>
		/// <exception cref="InvalidOperationException">The calling thread is not STA, or the window could not be created.</exception>
		public Win32Window(
			string? title = null,
			WINDOW_STYLE style = WindowStyles.TitleBar,
			WINDOW_EX_STYLE extendedStyle = WindowStyles.Composition,
			RECT? rect = null)
		{
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
			{
				throw new InvalidOperationException("A window must be created on an STA thread. Mark the entry point with [STAThread].");
			}

			Title = title ?? string.Empty;
			_style = style;
			_extendedStyle = extendedStyle;
			_initialRect = rect;

			WindowDpiHelper.EnsurePerMonitorDpiAwareness();
			WindowProcedure.EnsureRegistered();

			// WM_CREATE and the first WM_NCCALCSIZE arrive from inside CreateWindowExW, before a handle exists to map.
			WindowProcedure.BeginCreation(this);
			try
			{
				// Windows copies the class name and the caption, so both buffers are released as soon as the call returns.
				var className = NativeText.From(WindowProcedure.ClassName);
				var caption = NativeText.From(Title);
				try
				{
					Handle = Native.CreateWindowExW(
						_extendedStyle,
						className.Value,
						caption.Value,
						_style,
						rect?.left ?? CW_USEDEFAULT,
						rect?.top ?? CW_USEDEFAULT,
						rect?.Width ?? CW_USEDEFAULT,
						rect?.Height ?? CW_USEDEFAULT,
						HWND.Null,
						default,
						WindowProcedure.ModuleInstance,
						IntPtr.Zero);
				}
				finally
				{
					className.Dispose();
					caption.Dispose();
				}

				if (Handle.Value == 0)
				{
					throw new InvalidOperationException($"CreateWindowExW failed with Win32 error {Marshal.GetLastWin32Error()}.");
				}
			}
			finally
			{
				WindowProcedure.EndCreation();
			}

			WindowProcedure.Add(Handle, this);
			Dpi = WindowDpiHelper.GetWindowDpi(Handle);
			ApplyCornerPreference();
		}

		private const int CW_USEDEFAULT = unchecked((int)0x80000000);

		/// <summary>The window handle. Never zero after a successful construction.</summary>
		public HWND Handle { get; private set; }

		/// <summary>
		/// Records the handle Windows hands to the window procedure during creation. CreateWindowExW sends WM_NCCREATE,
		/// WM_NCCALCSIZE and WM_CREATE before it returns a value, and answering them through DefWindowProcW with the
		/// still-zero property fails (WM_NCCREATE returning FALSE is exactly the error-1400 creation failure), so the
		/// procedure adopts the live hwnd parameter into the property as soon as it is known.
		/// </summary>
		internal void AdoptCreationHandle(HWND hwnd) => Handle = hwnd;

		/// <summary>The current DPI of the window (96 at 100%, 144 at 150%).</summary>
		public uint Dpi { get; private set; }

		/// <summary>
		/// Whether Windows draws a caption on this window or the page owns the whole frame. Read straight off the styles
		/// given to <c>CreateWindowExW</c>, so it is fixed for the life of the HWND.
		/// </summary>
		internal WindowChrome Chrome => WindowStyles.HasTitleBar(_style) ? WindowChrome.TitleBar : WindowChrome.Borderless;

		/// <summary>Whether Windows draws this window's title bar. Convenience over <see cref="Chrome"/>.</summary>
		protected bool HasNativeTitleBar => Chrome == WindowChrome.TitleBar;

		/// <summary>The caption text (taskbar and Alt+Tab label), even though no frame draws it.</summary>
		public string Title
		{
			get => _title;
			set
			{
				_title = value ?? string.Empty;
				if (!_isDisposed && Handle.Value != 0)
				{
					var text = NativeText.From(_title);
					try
					{
						_ = Native.SetWindowTextW(Handle, text.Value);
					}
					finally
					{
						text.Dispose();
					}
				}
			}
		}

		/// <summary>
		/// Width of the resize band in device-independent pixels, defaulting to
		/// <see cref="WindowDpiHelper.DefaultResizeBorderDip"/> (the native frame's grab width). Clamped to at least 4 DIP
		/// by <see cref="WindowDpiHelper.GetResizeBorderPixels"/>, the floor requires.
		/// </summary>
		public int ResizeBorderDip { get; set; } = WindowDpiHelper.DefaultResizeBorderDip;

		/// <summary>
		/// Whether destroying this window ends the message loop of the thread (<c>PostQuitMessage</c>). Set it to
		/// <see langword="false"/> for every window when an application owns several and quits on its own terms.
		/// </summary>
		public bool QuitOnDestroy { get; set; } = true;

		/// <summary>The client rectangle in physical pixels. With the borderless frame this is the whole window.</summary>
		public RECT ClientRect
		{
			get
			{
				ObjectDisposedException.ThrowIf(_isDisposed, this);
				_ = Native.GetClientRect(Handle, out var rect);
				return rect;
			}
		}

		/// <summary>The window rectangle in screen coordinates.</summary>
		public RECT WindowRect
		{
			get
			{
				ObjectDisposedException.ThrowIf(_isDisposed, this);
				_ = Native.GetWindowRect(Handle, out var rect);
				return rect;
			}
		}

		/// <summary>Whether the window is shown.</summary>
		public bool IsVisible { get; private set; }

		/// <summary>True once a close has been requested and teardown (possibly asynchronous) is under way.</summary>
		public bool IsClosing => _closeRequested;

		/// <inheritdoc />
		public bool IsDisposed => _isDisposed;

		/// <summary>
		/// Raised when a message handler throws — an override of one of the <c>On*</c> methods, or a composition callback
		/// reached from one. The message that failed has already been answered with <c>DefWindowProcW</c>, so the window stays
		/// alive and keeps receiving input; what a subscriber decides is whether the exception is also promoted to the
		/// runtime's unhandled-exception path. Set <see cref="Win32UnhandledExceptionEventArgs.Handled"/> to say it has been
		/// dealt with; otherwise — and when there is no subscriber at all — it is promoted, because a failure that only
		/// disappears is worse than one a debugger or host crash handler can see.
		/// </summary>
		/// <remarks>
		/// Handlers run on the window's UI thread, so this event does too. It is process-wide rather than per instance: the
		/// point is to catch failures from windows an application never got a reference to, including its own subclasses that
		/// failed while being created.
		/// </remarks>
		public static event EventHandler<Win32UnhandledExceptionEventArgs>? UnhandledMessageException;

		/// <summary>
		/// Reports an exception that escaped a message handler. The window procedure calls it instead of letting the throw
		/// unwind: the frames above a window procedure belong to native <c>user32</c>, and with
		/// <c>DisableRuntimeMarshalling</c> there is no marshaler to catch anything either, so an escaping handler fails the
		/// process and nothing in <see cref="Run"/> can observe it.
		/// </summary>
		internal static void ReportHandlerFailure(Exception exception)
		{
			var args = new Win32UnhandledExceptionEventArgs(exception);
			try
			{
				UnhandledMessageException?.Invoke(null, args);
			}
			catch (Exception handlerException)
			{
				// A failing subscriber must not replace the original failure, and must not escape either — it is being called
				// from inside the same window procedure that is trying not to crash.
				Win32MessageQueue.RethrowAsUnhandled(new AggregateException(exception, handlerException));
				return;
			}

			if (!args.Handled)
			{
				Win32MessageQueue.RethrowAsUnhandled(exception);
			}
		}

		/// <summary>Shows the window, creating its visual on first use.</summary>
		public void Show()
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);
			_ = Native.ShowWindow(Handle, SHOW_WINDOW_CMD.SW_SHOW);
			_ = Native.UpdateWindow(Handle);
			IsVisible = true;
		}

		/// <summary>Hides the window without destroying it.</summary>
		public void Hide()
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);
			_ = Native.ShowWindow(Handle, SHOW_WINDOW_CMD.SW_HIDE);
			IsVisible = false;
		}

		/// <summary>Minimizes the window.</summary>
		public void Minimize() => _ = Native.ShowWindow(Handle, SHOW_WINDOW_CMD.SW_MINIMIZE);

		/// <summary>Maximizes the window to the monitor work area (see <see cref="OnGetMinMaxInfo"/>).</summary>
		public void Maximize() => _ = Native.ShowWindow(Handle, SHOW_WINDOW_CMD.SW_MAXIMIZE);

		/// <summary>Restores a minimized or maximized window.</summary>
		public void Restore() => _ = Native.ShowWindow(Handle, SHOW_WINDOW_CMD.SW_RESTORE);

		/// <summary>
		/// Starts the native window-move loop, as if the OS title bar had been grabbed. Call while a mouse button is
		/// still down — e.g. from a page drag region's mousedown handler — so Windows tracks the pointer until release.
		/// A maximized window cannot be moved this way; restore it first (see <see cref="Restore"/>).
		/// Only borderless windows need it — a window with a native caption (<see cref="TitleBarWin32Window"/>) is dragged by
		/// its own title bar, and calling this there just starts a second move loop.
		/// </summary>
		public void BeginDrag()
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			// ReleaseCapture hands the pointer to DefWindowProc, and the synthesized non-client down starts its modal
			// move loop. This is the standard recipe for hosts that let the page drive window movement.
			_ = Native.ReleaseCapture();
			_ = Native.SendMessageW(
				Handle,
				MessageDecoder.WM_NCLBUTTONDOWN,
				new WPARAM { Value = (nuint)HitTestResult.HTCAPTION },
				new LPARAM());
		}

		/// <summary>
		/// Requests close. The window answers with <see cref="OnCloseRequested"/>, which may defer destruction so
		/// asynchronous teardown (Razor component disposal) can finish on a live message loop.
		/// </summary>
		public void Close() => _ = Native.PostMessageW(Handle, MessageDecoder.WM_CLOSE);

		/// <summary>
		/// How Windows rounds the corners of this borderless window. Defaults to <see cref="WindowCornerPreference.Round"/>;
		/// set it before showing the window (DWM reads it once per change, and a maximized window is never rounded).
		/// </summary>
		public WindowCornerPreference CornerPreference { get; set; } = WindowCornerPreference.Round;

		private unsafe void ApplyCornerPreference()
		{
			var preference = (int)CornerPreference;

			// DWM ignores the call on Windows 10 and older, which is why this must not be fatal.
			_ = Native.DwmSetWindowAttribute(
				Handle,
				DWMWA_WINDOW_CORNER_PREFERENCE,
				(nint)(&preference),
				sizeof(int));
		}

		private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;

		/// <summary>
		/// Runs the message loop of this thread until <c>WM_QUIT</c>. Must be called on the window's thread.
		/// </summary>
		/// <returns>The exit code carried by <c>WM_QUIT</c>, or <c>-1</c> if <c>GetMessageW</c> failed.</returns>
		/// <remarks>
		/// Returning also ends this thread's <see cref="Win32MessageQueue"/>: nothing can run on a queue whose pump is gone, so
		/// completing it turns a later post into <see cref="Win32DispatcherException"/> instead of work that waits forever. A host
		/// that pumps the messages itself owns the queue and has to complete it by hand.
		/// </remarks>
		public int Run()
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			try
			{
				return PumpUntilQuit();
			}
			finally
			{
				// Own the queue's lifetime: Run is what makes it usable, so Run leaving is what retires it. Dispose runs on the
				// owning thread, which is the only thread allowed to destroy the message-only window.
				Win32MessageQueue.Current?.Dispose();
			}
		}

		private int PumpUntilQuit()
		{
			MSG message;
			while (true)
			{
				var result = Native.GetMessageW(out message, HWND.Null, 0, 0);
				if (result.Value == 0)
				{
					// WM_QUIT: wParam carries the exit code.
					return (int)message.wParam.Value;
				}

				if (result.Value < 0)
				{
					return -1;
				}

				// A composition-hosted browser has no child window to receive keys, so it gets first refusal here; this is what
				// makes tab traversal, browser accelerators and IME text composition work. Handled messages still
				// dispatch, because the browser's own window procedure expects to see them too.
				unsafe
				{
					if (_composition?.TryTranslateAccelerator((nint)(&message)) != true)
					{
						_ = Native.TranslateMessage(in message);
					}
				}

				_ = Native.DispatchMessageW(in message);
			}
		}

		/// <summary>
		/// Attaches the composition surface and WebView host that renders into this window. Called once, before
		/// <see cref="Show"/>; the window then keeps the visual sized to the client area.
		/// </summary>
		internal void AttachComposition(CompositionWebViewHost host)
		{
			_composition = host ?? throw new ArgumentNullException(nameof(host));
		}

		/// <summary>
		/// Sets the cursor that <c>WM_SETCURSOR</c> applies while the pointer is over the client area, which is how a
		/// composition-hosted WebView shows its own text/link/resize cursors.
		/// </summary>
		internal void SetWebViewCursor(HCURSOR cursor) => _webViewCursor = cursor;

		/// <summary>Releases the window: destroys the HWND and detaches it from the message routing table.</summary>
		public void Dispose()
		{
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}

		/// <summary>Releases native resources. Overrides must call the base implementation.</summary>
		protected virtual void Dispose(bool disposing)
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;

			if (disposing)
			{
				WindowProcedure.Remove(Handle);
				_composition?.Dispose();
				_composition = null;

				if (!IsDisposingOrDestroyed())
				{
					_ = Native.DestroyWindow(Handle);
				}
			}
		}

		private bool IsDisposingOrDestroyed() => _isDisposed && !Native.IsWindow(Handle);

		/// <summary>
		/// Routes one message. Kept as a single switch so the handled set is auditable against.
		/// </summary>
		internal unsafe LRESULT DispatchMessage(uint message, WPARAM wParam, LPARAM lParam)
		{
			switch (message)
			{
				case MessageDecoder.WM_CREATE:
					OnCreate();

					// Windows auto-adds WS_BORDER|WS_DLGFRAME (caption bits) to windows carrying WS_SYSMENU with min/max boxes.
					// Our WM_NCCALCSIZE already says "client == window", but DWM keeps drawing the caption it assumed at creation
					// until a frame change forces a non-client recalculation — observed as a title bar that shows at startup and
					// only vanishes after minimize/restore — the same SetWindowPos(SWP_FRAMECHANGED) Microsoft's custom-frame samples use.
					WindowStyles.ApplyFrameChange(Handle);
					return 0;

				case MessageDecoder.WM_SIZE:
					OnResized(MessageParameters.IsMinimizeSize(wParam), MessageParameters.IsMaximizeSize(wParam), new SIZE(MessageParameters.ShortLow(lParam.Value), MessageParameters.ShortHigh(lParam.Value)));
					return 0;

				case MessageDecoder.WM_MOVE:
					OnMoved();
					return 0;

				case MessageDecoder.WM_DPICHANGED:
					HandleDpiChanged(wParam, lParam);
					return 0;

				case MessageDecoder.WM_NCCALCSIZE:
					return OnNonClientCalcSize(wParam, lParam);

				case MessageDecoder.WM_GETMINMAXINFO:
					OnGetMinMaxInfo(ref Unsafe.AsRef<MINMAXINFO>((void*)lParam.Value));
					return 0;

				case MessageDecoder.WM_NCHITTEST:
					return (LRESULT)OnNonClientHitTest(MessageParameters.ToScreenPoint(lParam));

				case MessageDecoder.WM_SETFOCUS:
					OnFocusChanged(setOrKill: true);
					return 0;

				case MessageDecoder.WM_KILLFOCUS:
					OnFocusChanged(setOrKill: false);
					return 0;

				case MessageDecoder.WM_ACTIVATE:
					// Negative margins tell DWM the whole frame is client area (the standard custom-frame recipe).
					// Without it a caption remnant can be drawn over our composed content —
					// which is exactly what must NOT happen when a native caption is wanted: there DWM owns the non-client area.
					if (Chrome == WindowChrome.Borderless)
					{
						ExtendDwmFrameIntoClientArea();
					}
					OnActivated((uint)(wParam.Value & 0xFFFF) != 0);
					return 0;

				case MessageDecoder.WM_SETCURSOR:
					return OnSetCursor(lParam);

				case MessageDecoder.WM_MOUSEMOVE:
					HandleMouseMove(wParam, lParam);
					return 0;

				case MessageDecoder.WM_MOUSELEAVE:
					_isMouseTracking = false;
					OnMouseLeave();
					return 0;

				// Touch and pen arrive as pointer messages. They are forwarded in addition to the mouse messages Windows
				// synthesizes, because the page needs real touch semantics (pinch, hover-free taps) to behave correctly.
				// When nothing took the contact — no browser yet, or a runtime that cannot accept pointers — the message must
				// still reach DefWindowProcW: that is what promotes it to the legacy mouse messages. Answering it with 0 here
				// would leave touch and pen doing nothing at all.
				case MessageDecoder.WM_POINTERDOWN:
				case MessageDecoder.WM_POINTERUPDATE:
				case MessageDecoder.WM_POINTERUP:
				case MessageDecoder.WM_POINTERENTER:
				case MessageDecoder.WM_POINTERLEAVE:
					return OnPointer(message, MessageParameters.PointerId(wParam))
						? 0
						: Native.DefWindowProcW(Handle, message, wParam, lParam);

				case MessageDecoder.WM_CAPTURECHANGED:
					// Capture was taken from us (a system drag, an Alt+Tab, a modal). The browser must stop tracking the button.
					if (_hasMouseCapture)
					{
						_hasMouseCapture = false;
						_composition?.SendMouseLeave();
					}

					return 0;

				case MessageDecoder.WM_NCRBUTTONUP:
				case MessageDecoder.WM_CONTEXTMENU:
					OnContextMenu(MessageParameters.ToScreenPoint(lParam));
					return 0;

				case MessageDecoder.WM_MOUSEWHEEL:
				case MessageDecoder.WM_MOUSEHWHEEL:
					_composition?.SendMouseWheel(
						message == MessageDecoder.WM_MOUSEHWHEEL,
						MessageParameters.WheelDelta(wParam),
						MessageParameters.KeyStates(wParam),
						ScreenToClient(MessageParameters.ToScreenPoint(lParam)));
					return 0;

				case MessageDecoder.WM_LBUTTONDOWN:
				case MessageDecoder.WM_LBUTTONUP:
				case MessageDecoder.WM_LBUTTONDBLCLK:
				case MessageDecoder.WM_RBUTTONDOWN:
				case MessageDecoder.WM_RBUTTONUP:
				case MessageDecoder.WM_RBUTTONDBLCLK:
				case MessageDecoder.WM_MBUTTONDOWN:
				case MessageDecoder.WM_MBUTTONUP:
				case MessageDecoder.WM_MBUTTONDBLCLK:
				case MessageDecoder.WM_XBUTTONDOWN:
				case MessageDecoder.WM_XBUTTONUP:
					// Capture keeps the drag alive when the pointer leaves the window mid-drag; without it the browser loses
					// the button and a text selection stops at the edge.
					if (MessageParameters.IsButtonDown(message))
					{
						SetMouseCapture();
					}

					// lParam of a client-area button message is ALREADY in client coordinates — unlike the wheel, which
					// arrives in screen coordinates. SendMouseInput wants client coordinates too (the official WebView2
					// sample converts the wheel point with ScreenToClient before sending), so forwarding must not convert.
					_composition?.SendMouseButton(
						message,
						MessageParameters.KeyStates(wParam),
						MessageParameters.ToPoint(lParam));

					if (MessageParameters.IsButtonUp(message) && !_hasButtonsPressed())
					{
						ReleaseMouseCapture();
					}

					return 0;

				case MessageDecoder.WM_CLOSE:
					HandleClose();
					return 0;

				case MessageDecoder.WM_DESTROY:
					WindowProcedure.Remove(Handle);
					OnDestroyed();

					// The HWND is gone whoever destroyed it, so the managed wrapper is dead too. Running Dispose here (it is
					// idempotent, and skips DestroyWindow because IsWindow is now false) makes owners that wait on IsDisposed —
					// the deferred-close pattern in particular, where WM_CLOSE only starts an async teardown — observe the
					// teardown without having to call Dispose themselves.
					Dispose();

					if (QuitOnDestroy)
					{
						Native.PostQuitMessage(0);
					}

					return 0;

				default:
					return Native.DefWindowProcW(Handle, message, wParam, lParam);
			}
		}

		/// <summary>Converts a screen point to this window's client coordinates.</summary>
		protected POINT ScreenToClient(POINT screenPoint)
		{
			var point = screenPoint;
			_ = Native.ScreenToClient(Handle, ref point);
			return point;
		}

		/// <summary>Converts a client point to screen coordinates.</summary>
		protected POINT ClientToScreen(POINT clientPoint)
		{
			var point = clientPoint;
			_ = Native.ClientToScreen(Handle, ref point);
			return point;
		}

		/// <summary>Called from <c>WM_CREATE</c>, before the window is shown.</summary>
		protected virtual void OnCreate()
		{
		}

		/// <summary>
		/// Extends the DWM frame into the whole client area with -1 margins, so DWM never draws its own caption or border
		/// over the composition content.
		/// </summary>
		private void ExtendDwmFrameIntoClientArea()
		{
			var margins = new MARGINS
			{
				cxLeftWidth = -1,
				cxRightWidth = -1,
				cyTopHeight = -1,
				cyBottomHeight = -1,
			};

			_ = Native.DwmExtendFrameIntoClientArea(Handle, margins);
		}

		/// <summary>
		/// Called from <c>WM_SIZE</c>. The default keeps the composition visual stretched across the client area.
		/// </summary>
		/// <param name="isMinimized">The window was minimized; rendering can be throttled.</param>
		/// <param name="isMaximized">The window is now maximized.</param>
		/// <param name="clientSize">New client size in physical pixels.</param>
		protected virtual void OnResized(bool isMinimized, bool isMaximized, SIZE clientSize)
		{
			if (isMinimized)
			{
				_composition?.SetVisible(false);
				return;
			}

			_composition?.SetVisible(true);
			_composition?.Resize(ClientRect);
		}

		/// <summary>
		/// Called from <c>WM_MOVE</c>. The default tells the browser its parent moved, which is what keeps anchored popups
		/// (select dropdowns, date pickers) glued to the page while the window travels between monitors.
		/// </summary>
		protected virtual void OnMoved() => _composition?.NotifyParentWindowPositionChanged();

		/// <summary>
		/// Called for every <c>WM_POINTER*</c> message of a touch or pen contact. The default forwards it to the browser.
		/// </summary>
		/// <param name="message">The pointer message.</param>
		/// <param name="pointerId">The id identifying this contact across its whole lifecycle.</param>
		/// <returns>
		/// <see langword="true"/> when the contact was consumed and the message is answered; <see langword="false"/> to have
		/// the window answer it through <c>DefWindowProcW</c>, which is what turns a contact into mouse input on a runtime that
		/// cannot take pointers. Return <see langword="true"/> only if you handled the contact yourself.
		/// </returns>
		protected virtual bool OnPointer(uint message, uint pointerId) => _composition?.SendPointer(message, pointerId) ?? false;

		/// <summary>
		/// Called from <c>WM_CONTEXTMENU</c> / non-client right-click. The default opens the window's system menu, which a
		/// borderless window would otherwise give no way to reach.
		/// </summary>
		protected virtual void OnContextMenu(POINT screenPoint)
		{
			// A right-click inside the page belongs to the page (its own context menu, or none); only the resize band and
			// the title area we do not draw get the system menu.
			if (HitTestResult.HTCLIENT == OnNonClientHitTest(screenPoint))
			{
				return;
			}

			ShowSystemMenu(screenPoint);
		}

		/// <summary>Shows the standard restore/move/size/close menu at a screen point.</summary>
		public void ShowSystemMenu(POINT screenPoint)
		{
			var menu = Native.GetSystemMenu(Handle, false);
			if (menu.Value == 0)
			{
				return;
			}

			// The menu has to be re-laid-out for the current window state or "Maximize" stays enabled on a maximized window.
			var isMaximized = (bool)Native.IsZoomed(Handle);
			var isMinimized = (bool)Native.IsIconic(Handle);

			_ = Native.EnableMenuItem(menu, ExtraWindowMessages.SC_MINIMIZE, isMaximized ? MENU_ITEM_FLAGS.MF_GRAYED : 0);
			_ = Native.EnableMenuItem(menu, ExtraWindowMessages.SC_MAXIMIZE, isMaximized || isMinimized ? MENU_ITEM_FLAGS.MF_GRAYED : 0);
			_ = Native.EnableMenuItem(menu, ExtraWindowMessages.SC_RESTORE, isMaximized || isMinimized ? 0 : MENU_ITEM_FLAGS.MF_GRAYED);

			var command = Native.TrackPopupMenuEx(
				menu,
				ExtraWindowMessages.TPM_RETURNCMD | ExtraWindowMessages.TPM_RIGHTBUTTON,
				screenPoint.x,
				screenPoint.y,
				Handle,
				IntPtr.Zero);

			// With TPM_RETURNCMD the chosen command id comes back through the BOOL result; zero means nothing was picked.
			if ((long)command.Value != 0)
			{
				_ = Native.PostMessageW(Handle, MessageDecoder.WM_SYSCOMMAND, (WPARAM)(uint)command.Value);
			}
		}

		/// <summary>
		/// Called after a <c>WM_DPICHANGED</c> was applied. The default resizes the visual and re-runs hit-test math.
		/// </summary>
		/// <param name="newDpi">The new DPI of the window.</param>
		protected virtual void OnDpiChanged(uint newDpi) => _composition?.Resize(ClientRect);

		/// <summary>
		/// Called from <c>WM_NCCALCSIZE</c>. With a native title bar the default answer is Windows' own, so the client area
		/// really stops below the caption and the composition visual cannot overlap it. Borderless removes the whole
		/// non-client frame — which is what lets HTML reach the window edges — and when maximized clamps the client area to
		/// the monitor work area so content does not run under where the taskbar would be.
		/// </summary>
		protected unsafe virtual LRESULT OnNonClientCalcSize(WPARAM wParam, LPARAM lParam)
		{
			if (wParam.Value == 0 || HasNativeTitleBar)
			{
				return Native.DefWindowProcW(Handle, MessageDecoder.WM_NCCALCSIZE, wParam, lParam);
			}

			ref var parameters = ref Unsafe.AsRef<NCCALCSIZE_PARAMS>((void*)lParam.Value);
			if (Native.IsZoomed(Handle))
			{
				parameters.rgrc[0] = GetMonitorWorkArea();
			}

			// Returning 0 with an unmodified rgrc[0] means "the client area is the whole window".
			return 0;
		}

		/// <summary>
		/// Called from <c>WM_GETMINMAXINFO</c>. Borderless maximizes to the monitor work area (it has no frame of its own to
		/// account for); with a native caption Windows does that arithmetic itself, so only the sane minimum tracking size is
		/// applied.
		/// </summary>
		protected virtual void OnGetMinMaxInfo(ref MINMAXINFO info)
		{
			var monitor = GetMonitorInfo();
			if (monitor is null)
			{
				return;
			}

			var workArea = monitor.Value.workArea;
			var monitorBounds = monitor.Value.monitorBounds;

			if (!HasNativeTitleBar)
			{
				info.ptMaxSize.x = workArea.right - workArea.left;
				info.ptMaxSize.y = workArea.bottom - workArea.top;
				info.ptMaxPosition.x = workArea.left - monitorBounds.left;
				info.ptMaxPosition.y = workArea.top - monitorBounds.top;
			}

			var minimumWidth = WindowDpiHelper.DipToPixel(200, Dpi);
			var minimumHeight = WindowDpiHelper.DipToPixel(120, Dpi);
			info.ptMinTrackSize.x = Math.Max(info.ptMinTrackSize.x, minimumWidth);
			info.ptMinTrackSize.y = Math.Max(info.ptMinTrackSize.y, minimumHeight);
		}

		/// <summary>
		/// Called from <c>WM_NCHITTEST</c>. With a native caption the answer is Windows', because it knows where its own
		/// caption, buttons and frame are — that is what gives caption drag, snap layouts and double-click maximize. The
		/// borderless default claims the thin outer band as a resize target and leaves the rest of the window to the WebView
		/// (input region, never a rendering inset).
		/// </summary>
		protected virtual int OnNonClientHitTest(POINT screenPoint)
		{
			if (HasNativeTitleBar)
			{
				// DefWindowProcW wants the screen point as a MAKELPARAM-style LPARAM; FromScreenPoint is the exact inverse of
				// the decode that produced this argument, which matters — a wrong shift makes every frame pixel HTNOWHERE and
				// silently costs the window its resize borders (a regression check for this lives in the lifetime harness).
				return (int)Native.DefWindowProcW(
					Handle,
					MessageDecoder.WM_NCHITTEST,
					default,
					MessageParameters.FromScreenPoint(screenPoint)).Value;
			}

			var border = WindowDpiHelper.GetResizeBorderPixels(ResizeBorderDip, Dpi);

			// A maximized window has no resize edges.
			if (Native.IsZoomed(Handle))
			{
				return HitTestResult.HTCLIENT;
			}

			return WindowHitTest.HitTest(screenPoint, WindowRect, border);
		}

		/// <summary>Called from <c>WM_SETFOCUS</c> and <c>WM_KILLFOCUS</c>.</summary>
		protected virtual void OnFocusChanged(bool setOrKill)
		{
			if (setOrKill)
			{
				_composition?.MoveFocus();
			}
		}

		/// <summary>Called from <c>WM_ACTIVATE</c>.</summary>
		protected virtual void OnActivated(bool isActive)
		{
		}

		/// <summary>Called from <c>WM_CLOSE</c>.</summary>
		/// <returns>
		/// <see langword="true"/> when destruction is deferred — the owner must then call <see cref="DestroyWindow"/>
		/// itself once asynchronous teardown completes. <see langword="false"/> destroys the window immediately.
		/// </returns>
		protected virtual bool OnCloseRequested() => false;

		/// <summary>Called from <c>WM_DESTROY</c>, after the window left the routing table.</summary>
		protected virtual void OnDestroyed()
		{
		}

		/// <summary>Called for every client-area <c>WM_MOUSEMOVE</c>.</summary>
		protected virtual void OnMouseMove(uint keyStates, POINT clientPoint) => _composition?.SendMouseMove(keyStates, clientPoint);

		/// <summary>Called when the pointer left the client area (after <c>TrackMouseEvent</c> armed it).</summary>
		protected virtual void OnMouseLeave() => _composition?.SendMouseLeave();

		/// <summary>Destroys the HWND. Used by owners that deferred destruction from <see cref="OnCloseRequested"/>.</summary>
		protected void DestroyWindow() => _ = Native.DestroyWindow(Handle);

		/// <summary>
		/// Takes mouse capture so a drag that leaves the window keeps producing messages for the page.
		/// </summary>
		private void SetMouseCapture()
		{
			if (!_hasMouseCapture && Native.SetCapture(Handle).Value != 0)
			{
				_hasMouseCapture = true;
			}
		}

		private void ReleaseMouseCapture()
		{
			if (_hasMouseCapture)
			{
				_hasMouseCapture = false;
				_ = Native.ReleaseCapture();
			}
		}

		/// <summary>True while any mouse button is still down, read from the async key state.</summary>
		private bool _hasButtonsPressed()
		{
			foreach (var virtualKey in new[] { VIRTUAL_KEY.VK_LBUTTON, VIRTUAL_KEY.VK_RBUTTON, VIRTUAL_KEY.VK_MBUTTON, VIRTUAL_KEY.VK_XBUTTON1, VIRTUAL_KEY.VK_XBUTTON2 })
			{
				if ((Native.GetAsyncKeyState((int)virtualKey) & unchecked((short)0x8000)) != 0)
				{
					return true;
				}
			}

			return false;
		}

		private void HandleMouseMove(WPARAM wParam, LPARAM lParam)
		{
			if (!_isMouseTracking)
			{
				var tracking = new TRACKMOUSEEVENT
				{
					cbSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<TRACKMOUSEEVENT>(),
					dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
					hwndTrack = Handle,
				};

				_isMouseTracking = Native.TrackMouseEvent(ref tracking);
			}

			// While captured, moves outside the window still arrive; they are client-relative and negative, which is exactly
			// what the browser expects for a drag that continued past the edge.
			OnMouseMove(MessageParameters.KeyStates(wParam), MessageParameters.ToPoint(lParam));
		}

		private LRESULT OnSetCursor(LPARAM lParam)
		{
			// The low word of lParam is the hit-test area, the next word the mouse message that caused this.
			var hitTest = (short)(lParam.Value & 0xFFFF);
			if (hitTest == HitTestResult.HTCLIENT && _webViewCursor.Value != 0)
			{
				_ = Native.SetCursor(_webViewCursor);
				return 1; // handled: keep the WebView's cursor instead of the class cursor
			}

			// Over the resize band, set the stock size cursor ourselves. DefWindowProc does not map our synthesized
			// non-client hit codes to one (verified live: hovering HTRIGHT kept the arrow), which is exactly why Microsoft's
			// custom-frame sample and Chrome's message handler do this by hand.
			var resizeCursor = ResizeCursorFor(hitTest);
			if (resizeCursor != 0)
			{
				_ = Native.SetCursor(new HCURSOR(resizeCursor));
				return 1;
			}

			return Native.DefWindowProcW(Handle, MessageDecoder.WM_SETCURSOR, (WPARAM)_webViewCursor.Value, lParam);
		}

		/// <summary>The stock resize cursor for a hit-test code, or zero when the code is not a resize edge/corner.</summary>
		private static nint ResizeCursorFor(short hitTest) => hitTest switch
		{
			HitTestResult.HTLEFT or HitTestResult.HTRIGHT => LoadCursorW(IntPtr.Zero, IdcSizeWe),
			HitTestResult.HTTOP or HitTestResult.HTBOTTOM => LoadCursorW(IntPtr.Zero, IdcSizeNs),
			HitTestResult.HTTOPLEFT or HitTestResult.HTBOTTOMRIGHT => LoadCursorW(IntPtr.Zero, IdcSizeNwse),
			HitTestResult.HTTOPRIGHT or HitTestResult.HTBOTTOMLEFT => LoadCursorW(IntPtr.Zero, IdcSizeNesw),
			_ => 0,
		};

		// MAKEINTRESOURCE cursor ids (Winuser.h). Loading by id returns the shared system cursor instance.
		private const uint IdcSizeNwse = 32642;
		private const uint IdcSizeNesw = 32643;
		private const uint IdcSizeWe = 32644;
		private const uint IdcSizeNs = 32645;

		[DllImport("user32.dll")]
		private static extern nint LoadCursorW(IntPtr hInstance, uint lpCursorName);

		private unsafe void HandleDpiChanged(WPARAM wParam, LPARAM lParam)
		{
			Dpi = (uint)MessageParameters.DevicePixelRatio(wParam).X;

			// Windows proposes a new window rectangle that preserves the physical size; honour it.
			var suggested = Unsafe.Read<RECT>((void*)lParam.Value);
			_ = Native.SetWindowPos(Handle, HWND.Null, suggested.left, suggested.top, suggested.Width, suggested.Height,
				SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

			OnDpiChanged(Dpi);
		}

		private void HandleClose()
		{
			if (_closeRequested)
			{
				// A second WM_CLOSE while teardown runs is ignored: the first one owns the shutdown.
				return;
			}

			_closeRequested = true;
			if (!OnCloseRequested())
			{
				DestroyWindow();
			}
		}

		private RECT GetMonitorWorkArea() => GetMonitorInfo()?.workArea ?? ClientRect;

		private unsafe (RECT monitorBounds, RECT workArea)? GetMonitorInfo()
		{
			var monitor = Native.MonitorFromWindow(Handle, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
			if (monitor.Value == 0)
			{
				return null;
			}

			var info = new MONITORINFO { cbSize = (uint)Unsafe.SizeOf<MONITORINFO>() };
			return Native.GetMonitorInfoW(monitor, ref info) ? (info.rcMonitor, info.rcWork) : null;
		}
	}
}
