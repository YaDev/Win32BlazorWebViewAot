// A Blazor window whose page owns the whole surface, caption included.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// A borderless Win32 window whose client area is a Blazor application: no OS caption is drawn, so HTML reaches every
	/// edge and anything resembling a title bar belongs to the page. Drag it from a caption region's mouse-down handler
	/// with <see cref="Win32Window.BeginDrag"/>; resize edges, snap layouts and DPI behaviour stay native.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Same lifetime rules as <see cref="BlazorWebViewWindow"/> — close defers destruction so asynchronous component
	/// disposal runs against a live message loop. The only difference is the frame, which is fixed when the window is
	/// created and cannot be switched later.
	/// </para>
	/// <example>
	/// <code>
	/// using var window = new BorderlessBlazorWebViewWindow("My App")
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
	/// </remarks>
	public class BorderlessBlazorWebViewWindow : BlazorWebViewWindow
	{
		/// <summary>Creates a borderless window and the view that fills it.</summary>
		/// <param name="title">The caption (taskbar and Alt+Tab label), even though no frame draws it.</param>
		/// <param name="rect">Initial size in physical pixels, or <see langword="null"/> for the system default.</param>
		public BorderlessBlazorWebViewWindow(string? title = null, RECT? rect = null)
			: base(title, WindowStyles.Borderless, WindowStyles.Composition, rect)
		{
		}
	}
}
