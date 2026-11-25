// A window whose top bar is drawn by Windows.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// A top-level window with the normal Windows title bar: caption text, min/max/close buttons, snap layouts on hover,
	/// double-click maximize and the Alt+Space system menu are all provided by the OS. The composed content fills the
	/// client area below the caption.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is what <see cref="Win32Window"/> does when constructed with no style argument; the type exists so the choice
	/// is visible where a window is created and cannot be changed afterwards (styles are fixed at
	/// <c>CreateWindowExW</c> time). Use <see cref="BorderlessWin32Window"/> instead when the page draws its own bar.
	/// </para>
	/// <example>
	/// <code>
	/// using var window = new TitleBarWin32Window("My App");
	/// window.Show();
	/// return window.Run();
	/// </code>
	/// </example>
	/// </remarks>
	public class TitleBarWin32Window : Win32Window
	{
		/// <summary>Creates a window carrying the standard Windows caption and frame.</summary>
		/// <param name="title">Caption text (taskbar, Alt+Tab and the title bar itself).</param>
		/// <param name="rect">Initial size in physical pixels, or <see langword="null"/> for the system default.</param>
		public TitleBarWin32Window(string? title = null, RECT? rect = null)
			: base(title, WindowStyles.TitleBar, WindowStyles.Composition, rect)
		{
		}
	}
}
