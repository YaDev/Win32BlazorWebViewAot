using System;
using Win32BlazorWebViewAot;

namespace Win32BlazorWebViewAot.BlazorBorderless {
	/// <summary>
	/// Handle on the main window for components: the title bar drives the window through this service because a
	/// borderless window has no OS caption — dragging, minimizing and closing must come from the page itself.
	/// The window is created after the service provider (it needs <c>Services</c>), so it attaches to the holder
	/// instead of the other way around.
	/// </summary>
	public sealed class AppWindow
	{
		private Win32Window? _window;
		private bool _isMaximized;

		internal void Attach(Win32Window window) => _window = window;

		/// <summary>
		/// Starts the native move loop. Called from the title bar's mousedown, while the button is still down —
		/// Windows then tracks the pointer itself until it is released (custom chrome owns the caption).
		/// </summary>
		public void BeginDrag() => _window?.BeginDrag();

		public void Minimize() => _window?.Minimize();

		public void Close() => _window?.Close();

		/// <summary>Maximizes to the work area, or restores when already maximized (title-bar double-click).</summary>
		public void ToggleMaximize()
		{
			if (_window is null)
			{
				return;
			}

			if (_isMaximized)
			{
				_window.Restore();
			}
			else
			{
				_window.Maximize();
			}

			_isMaximized = !_isMaximized;
		}
	}
}
