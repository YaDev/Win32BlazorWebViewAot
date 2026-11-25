// Window message constants and wParam/lParam decoding helpers.
// DirectNAot exposes most WM_* values on MessageDecoder; this type adds what it does not (hit-test results,
// a couple of messages) and centralizes the awkward lParam unpacking so WndProc code reads as intent.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Hit-test results returned from <c>WM_NCHITTEST</c>.
	/// </summary>
	internal static class HitTestResult
	{
		public const int HTERROR = -2;
		public const int HTNOWHERE = 0;
		public const int HTCLIENT = 1;
		public const int HTCAPTION = 2;
		public const int HTSYSMENU = 3;
		public const int HTMINBUTTON = 8;
		public const int HTMAXBUTTON = 9;
		public const int HTCLOSE = 20;
		public const int HTLEFT = 10;
		public const int HTRIGHT = 11;
		public const int HTTOP = 12;
		public const int HTTOPLEFT = 13;
		public const int HTTOPRIGHT = 14;
		public const int HTBOTTOM = 15;
		public const int HTBOTTOMLEFT = 16;
		public const int HTBOTTOMRIGHT = 17;
	}

	/// <summary>
	/// The subset of window messages this host handles that DirectNAot's <see cref="MessageDecoder"/> does not name.
	/// </summary>
	internal static class ExtraWindowMessages
	{
		public const uint WM_GETOBJECT = 0x003D;
		public const uint WM_NCMOUSELEAVE = 0x02A2;

		// System-menu commands (user32 SC_*). DirectNAot does not project them; these are stable ABI values.
		public const uint SC_CLOSE = 0xF060;
		public const uint SC_MINIMIZE = 0xF020;
		public const uint SC_MAXIMIZE = 0xF030;
		public const uint SC_RESTORE = 0xF120;

		// TrackPopupMenuEx flags: return the chosen command instead of sending it, and let a left-click behave like a right-
		// click so the menu does not dismiss while the pointer travels to it.
		public const uint TPM_RETURNCMD = 0x0100;
		public const uint TPM_RIGHTBUTTON = 0x0002;

		// WM_APP range: the first slot carries "work is queued on this thread" (see Win32MessageQueue).
		public const uint WM_APP = 0x8000;
	}

	/// <summary>
	/// Decodes the packed coordinates and flags that arrive in <c>WPARAM</c>/<c>LPARAM</c>.
	/// </summary>
	internal static class MessageParameters
	{
		// The 16-bit halves of a message parameter. Both halves are taken sign-extended, which is what LOWORD+(SHORT)
		// does in C and what makes negative coordinates from WM_MOUSEMOVE / WM_NCHITTEST land where the pointer is.
		private static int Low16(long value) => (short)(value & 0xFFFF);

		private static int High16(long value) => (short)((value >> 16) & 0xFFFF);

		/// <summary>Low-order 16 signed bits of a <c>LPARAM</c>-sized value.</summary>
		public static int ShortLow(nint value) => Low16(value);

		/// <summary>High-order 16 signed bits of a <c>LPARAM</c>-sized value.</summary>
		public static int ShortHigh(nint value) => High16(value);

		/// <summary>Low-order 16 signed bits of a <c>WPARAM</c>-sized value.</summary>
		public static int ShortLow(nuint value) => Low16((long)value);

		/// <summary>High-order 16 signed bits of a <c>WPARAM</c>-sized value.</summary>
		public static int ShortHigh(nuint value) => High16((long)value);

		/// <summary>Client (or screen, for non-client messages) point packed into <c>LPARAM</c>.</summary>
		public static POINT ToPoint(LPARAM lParam) => new(Low16(lParam.Value), High16(lParam.Value));

		/// <summary>Screen point packed into an <c>WM_NCHITTEST</c>-style <c>LPARAM</c>.</summary>
		public static POINT ToScreenPoint(LPARAM lParam) => ToPoint(lParam);

		/// <summary>
		/// The inverse of <see cref="ToScreenPoint"/>: a screen point as the <c>LPARAM</c> Win32 expects.
		/// </summary>
		/// <remarks>
		/// <c>MAKELPARAM</c> puts x in the LOW word and y in the high word of a 32-bit value, which is then sign-extended
		/// into the 64-bit <c>LPARAM</c> — shifting y by 32 instead leaves y reading as zero, and a point at y = 0 is above
		/// every window: <c>DefWindowProcW(WM_NCHITTEST)</c> then answers <c>HTNOWHERE</c> for the whole frame, so a native
		/// caption window loses its resize borders. Encode/decode live together so they cannot drift apart again.
		/// </remarks>
		public static LPARAM FromScreenPoint(POINT point) =>
			unchecked(new LPARAM((nint)(((uint)(ushort)point.y << 16) | (ushort)point.x)));

		/// <summary>The wheel delta of <c>WM_MOUSEWHEEL</c>/<c>WM_MOUSEHWHEEL</c> (high word of wParam).</summary>
		public static int WheelDelta(WPARAM wParam) => ShortHigh(wParam.Value);

		/// <summary>Modifier keys pressed during a mouse or wheel message (low word of wParam).</summary>
		public static uint KeyStates(WPARAM wParam) => (uint)(wParam.Value & 0xFFFF);

		/// <summary>The extended button identifier of <c>WM_XBUTTON*</c> (high word of wParam).</summary>
		public static int XButton(WPARAM wParam) => ShortHigh(wParam.Value);

		/// <summary>
		/// True for the press half of a button pair (<c>*BUTTONDOWN</c>, <c>*BUTTONDBLCLK</c>). Double-click counts as a press:
		/// capture has to be (re)taken, not left over from the first click.
		/// </summary>
		public static bool IsButtonDown(uint message) =>
			message is MessageDecoder.WM_LBUTTONDOWN or MessageDecoder.WM_RBUTTONDOWN or MessageDecoder.WM_MBUTTONDOWN
				or MessageDecoder.WM_XBUTTONDOWN
				or MessageDecoder.WM_LBUTTONDBLCLK or MessageDecoder.WM_RBUTTONDBLCLK or MessageDecoder.WM_MBUTTONDBLCLK;

		/// <summary>True for the release half of a button pair.</summary>
		public static bool IsButtonUp(uint message) =>
			message is MessageDecoder.WM_LBUTTONUP or MessageDecoder.WM_RBUTTONUP or MessageDecoder.WM_MBUTTONUP
				or MessageDecoder.WM_XBUTTONUP;

		/// <summary>The pointer id of a <c>WM_POINTER*</c> message (low word of wParam).</summary>
		public static uint PointerId(WPARAM wParam) => (uint)(wParam.Value & 0xFFFF);

		/// <summary>True when the low bit of wParam marks a maximized-size event (<c>SIZE_MAXIMIZED</c>).</summary>
		public static bool IsMaximizeSize(WPARAM wParam) => (uint)(wParam.Value & 0xFFFF) == 2;

		/// <summary>True when the low bit of wParam marks a minimized-size event (<c>SIZE_MINIMIZED</c>).</summary>
		public static bool IsMinimizeSize(WPARAM wParam) => (uint)(wParam.Value & 0xFFFF) == 1;

		/// <summary>The new DPI pair packed into <c>WM_DPICHANGED</c>'s wParam.</summary>
		public static (int X, int Y) DevicePixelRatio(WPARAM wParam) => (ShortLow(wParam.Value), ShortHigh(wParam.Value));
	}
}
