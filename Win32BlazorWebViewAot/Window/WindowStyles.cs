// The style set of a borderless, composition-rendered top-level window.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Window styles used by <see cref="Win32Window"/>.
	/// </summary>
	public static class WindowStyles
	{
		/// <summary>
		/// A resizable top-level window with no drawn non-client frame: <c>WS_THICKFRAME</c> keeps native resizing and
		/// snap layouts alive, while the caption, border and menu are removed in <c>WM_NCCALCSIZE</c>. The system menu
		/// and min/max boxes stay in the style so Alt+Space, taskbar jump lists and keyboard shortcuts keep working even
		/// though nothing draws them.
		/// </summary>
		public const WINDOW_STYLE Borderless =
			WINDOW_STYLE.WS_THICKFRAME |
			WINDOW_STYLE.WS_MINIMIZEBOX |
			WINDOW_STYLE.WS_MAXIMIZEBOX |
			WINDOW_STYLE.WS_SYSMENU;

		/// <summary>
		/// A normal top-level window: Windows draws the caption and the frame, so min/max/close, snap layouts, the system
		/// menu and double-click maximize all come from the OS. The composition visual fills the client area, which this
		/// style set leaves below the caption (see <see cref="WindowChrome"/>).
		/// </summary>
		public const WINDOW_STYLE TitleBar =
			WINDOW_STYLE.WS_CAPTION |
			WINDOW_STYLE.WS_THICKFRAME |
			WINDOW_STYLE.WS_MINIMIZEBOX |
			WINDOW_STYLE.WS_MAXIMIZEBOX |
			WINDOW_STYLE.WS_SYSMENU;

		/// <summary>
		/// Whether a style set asks Windows to draw a caption. Single source of truth for the chrome branches in
		/// <see cref="Win32Window"/>, so they can never disagree with the styles the window was really created with.
		/// </summary>
		/// <param name="style">The styles passed to <c>CreateWindowExW</c>.</param>
		/// <returns><see langword="true"/> when <c>WS_CAPTION</c> is present.</returns>
		public static bool HasTitleBar(WINDOW_STYLE style) =>
			(style & WINDOW_STYLE.WS_CAPTION) == WINDOW_STYLE.WS_CAPTION;

		/// <summary>
		/// Extended style of a window whose pixels come from the composition tree: <c>WS_EX_NOREDIRECTIONBITMAP</c>
		/// tells Windows not to allocate a redirection surface for the HWND, because everything visible is composed.
		/// </summary>
		public const WINDOW_EX_STYLE Composition = WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP;

		/// <summary>
		/// Applies the frame change after <c>WM_NCCALCSIZE</c> handling changes the client area, so Windows recomputes
		/// (and stops drawing) the old non-client border.
		/// </summary>
		public static void ApplyFrameChange(HWND hwnd) =>
			_ = Native.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
				SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
				SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
				SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
				SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
				SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);
	}
}
