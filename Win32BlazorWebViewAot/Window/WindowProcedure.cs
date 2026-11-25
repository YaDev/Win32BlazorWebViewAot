// One window class per process, one WNDPROC, and a table that routes a message back to the managed window that owns
// the HWND. The delegate is rooted statically: it is handed to Windows as a raw function pointer, so nothing else in
// the runtime keeps it alive.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Registers the native window class used by <see cref="Win32Window"/> and dispatches its messages.
	/// </summary>
	internal static class WindowProcedure
	{
		/// <summary>The registered window class name.</summary>
		internal const string ClassName = "Win32BlazorWebViewAot.Window";

		private static readonly Lock Gate = new();
		private static readonly Dictionary<nint, Win32Window> Instances = new();

		// The window being created. CreateWindowExW sends WM_CREATE / WM_NCCALCSIZE synchronously, before it has a
		// handle to return and therefore before the HWND can be mapped; creation is serialized on the creating thread,
		// so exactly one window can be in this state at a time.
		[ThreadStatic]
		private static Win32Window? _creating;

		// Rooted: Windows calls through this pointer for as long as any window of the class exists.
		private static WNDPROC? _wndProc;
		private const nint IdcArrow = 32512;
		private static ushort _classAtom;
		private static HINSTANCE _module;

		/// <summary>The module instance to pass to <c>CreateWindowExW</c>.</summary>
		public static HINSTANCE ModuleInstance => _module;

		/// <summary>
		/// Registers the window class once per process.
		/// </summary>
		/// <exception cref="InvalidOperationException">The class could not be registered.</exception>
		public static void EnsureRegistered()
		{
			lock (Gate)
			{
				if (_classAtom != 0)
				{
					return;
				}

				_module = (HINSTANCE)(nint)Native.GetModuleHandleW(default);
				_wndProc = new WNDPROC(WndProc);

				var className = NativeText.From(ClassName);
				var windowClass = new WNDCLASSW
				{
					style = WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
					lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
					cbClsExtra = 0,
					cbWndExtra = 0,
					hInstance = _module,
					hIcon = default,
					hCursor = Native.LoadCursorW(default, new PWSTR((nint)IdcArrow)), // MAKEINTRESOURCE(IDC_ARROW)
					hbrBackground = default, // composition supplies the pixels; a brush would only add flicker.
					lpszMenuName = default,
					lpszClassName = className.Value,
				};

				var atom = Native.RegisterClassW(in windowClass);

				// Registration copies the class name, so the buffer is ours to release immediately.
				className.Dispose();
				if (atom == 0)
				{
					var error = Marshal.GetLastWin32Error();

					// 1410 = ERROR_CLASS_ALREADY_EXISTS: another component in the process registered the same name.
					if (error != 1410)
					{
						throw new InvalidOperationException($"RegisterClassW failed with Win32 error {error}.");
					}
				}

				_classAtom = atom;
			}
		}

		/// <summary>Marks <paramref name="window"/> as the window being created, so creation-time messages reach it.</summary>
		public static void BeginCreation(Win32Window window) => _creating = window;

		/// <summary>Clears the creating-window marker once <c>CreateWindowExW</c> has returned.</summary>
		public static void EndCreation() => _creating = null;

		/// <summary>Associates <paramref name="hwnd"/> with its managed window. Call before the window receives messages.</summary>
		public static void Add(HWND hwnd, Win32Window window)
		{
			lock (Gate)
			{
				Instances[hwnd.Value] = window;
			}
		}

		/// <summary>Drops the association. After this the procedure falls back to <c>DefWindowProcW</c>.</summary>
		public static void Remove(HWND hwnd)
		{
			lock (Gate)
			{
				_ = Instances.Remove(hwnd.Value);
			}
		}

		private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
		{
			var window = _creating;
			if (window is not null)
			{
				// CreateWindowExW has no return value yet while it sends the creation messages; adopt the live handle so
				// handlers (and our DefWindowProcW fallback) never operate on a zero HWND. WM_NCCREATE answered through a
				// zero-handle DefWindowProcW returns FALSE, which fails CreateWindowExW with ERROR_INVALID_WINDOW_HANDLE.
				window.AdoptCreationHandle(hwnd);
			}
			else
			{
				lock (Gate)
				{
					_ = Instances.TryGetValue(hwnd.Value, out window);
				}
			}

			// Messages that arrive before the instance is known (or after it is gone) must still be answered: a window
			// procedure that does not call DefWindowProcW for them leaks default handling.
			if (window is null)
			{
				return Native.DefWindowProcW(hwnd, message, wParam, lParam);
			}

			try
			{
				return window.DispatchMessage(message, wParam, lParam);
			}
			catch (Exception exception)
			{
				// The exception boundary between Windows and managed code. Everything above this frame is native user32, so a
				// throw from a handler cannot be caught by the application's Run loop — it fails the process instead. Answer the
				// message the way an unhandled one is answered and route the failure to the reporting path.
				Win32Window.ReportHandlerFailure(exception);
				return Native.DefWindowProcW(hwnd, message, wParam, lParam);
			}
		}
	}
}
