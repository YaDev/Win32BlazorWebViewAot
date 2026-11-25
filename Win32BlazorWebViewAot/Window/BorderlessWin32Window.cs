// A window whose page owns the whole surface, including anything that looks like a title bar.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// A top-level window with no drawn non-client frame: the client area is the whole window, so HTML reaches every edge
	/// and a title bar has to be drawn by the page. Drag it with
	/// <see cref="Win32Window.BeginDrag"/> from the caption region's mouse-down handler; resize edges, snap layouts and
	/// DPI behaviour stay native.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The window answers <c>WM_NCCALCSIZE</c> with "client == window", tells DWM the frame is client area, and hit-tests
	/// only a thin outer band as resize. See <see cref="TitleBarWin32Window"/> for the opposite choice, where Windows
	/// draws the caption.
	/// </para>
	/// <example>
	/// <code>
	/// using var window = new BorderlessWin32Window("My App");
	/// window.Show();
	/// return window.Run();
	/// </code>
	/// </example>
	/// </remarks>
	public class BorderlessWin32Window : Win32Window
	{
		/// <summary>Creates a borderless window whose client area is the whole window.</summary>
		/// <param name="title">The caption (taskbar text and Alt+Tab label), even though no frame draws it.</param>
		/// <param name="rect">Initial size in physical pixels, or <see langword="null"/> for the system default.</param>
		public BorderlessWin32Window(string? title = null, RECT? rect = null)
			: base(title, WindowStyles.Borderless, WindowStyles.Composition, rect)
		{
		}
	}
}
