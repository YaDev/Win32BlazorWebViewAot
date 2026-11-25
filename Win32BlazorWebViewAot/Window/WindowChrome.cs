// Who draws the top of the window: Windows or the page.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Which frame a <see cref="Win32Window"/> has, derived from the styles it was created with — see
	/// <see cref="WindowStyles.HasTitleBar(WINDOW_STYLE)"/>.
	/// </summary>
	/// <remarks>
	/// The mode cannot change after construction: styles are fixed at <c>CreateWindowExW</c> time, and the first
	/// <c>WM_NCCALCSIZE</c> arrives from inside that call. That is why the public surface offers window types
	/// (<see cref="TitleBarWin32Window"/>, <see cref="BorderlessWin32Window"/>) rather than a settable property.
	/// </remarks>
	internal enum WindowChrome
	{
		/// <summary>
		/// The page owns the whole window: <c>WM_NCCALCSIZE</c> reports client == window, DWM is told the frame is client
		/// area, and hit-testing claims only a thin resize band. Anything resembling a title bar is drawn in HTML.
		/// </summary>
		Borderless,

		/// <summary>
		/// Windows owns the caption and frame: non-client messages go to <c>DefWindowProcW</c>, so caption drag, snap
		/// layouts, min/max/close and the system menu are the OS's own. The composition visual fills the client area left
		/// below the caption.
		/// </summary>
		TitleBar,
	}
}
