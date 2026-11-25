// Resize hit-testing for a window whose non-client frame is gone.
//
// The resize region is an INPUT region, not an excluded rendering region: the WebView visual still stretches across
// the whole client area, and only pointers that land inside these thin bands are claimed by Windows as resize drags.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Maps a pointer position to a <c>WM_NCHITTEST</c> result for a borderless window.
	/// </summary>
	internal static class WindowHitTest
	{
		/// <summary>
		/// Classifies <paramref name="screenPoint"/> against the window rectangle <paramref name="windowRect"/>.
		/// </summary>
		/// <param name="screenPoint">Pointer position in screen coordinates.</param>
		/// <param name="windowRect">The window rectangle (which, with our <c>WM_NCCALCSIZE</c>, is also the client area).</param>
		/// <param name="resizeBorderPixels">Width of the resize band in physical pixels; already DPI-scaled.</param>
		/// <returns>A <see cref="HitTestResult"/> value: a corner, an edge, or <see cref="HitTestResult.HTCLIENT"/>.</returns>
		public static int HitTest(POINT screenPoint, RECT windowRect, int resizeBorderPixels)
		{
			var left = Math.Abs(screenPoint.x - windowRect.left) < resizeBorderPixels;
			var right = Math.Abs(screenPoint.x - (windowRect.right - 1)) < resizeBorderPixels;
			var top = Math.Abs(screenPoint.y - windowRect.top) < resizeBorderPixels;
			var bottom = Math.Abs(screenPoint.y - (windowRect.bottom - 1)) < resizeBorderPixels;

			if (top && left)
			{
				return HitTestResult.HTTOPLEFT;
			}

			if (top && right)
			{
				return HitTestResult.HTTOPRIGHT;
			}

			if (bottom && left)
			{
				return HitTestResult.HTBOTTOMLEFT;
			}

			if (bottom && right)
			{
				return HitTestResult.HTBOTTOMRIGHT;
			}

			if (left)
			{
				return HitTestResult.HTLEFT;
			}

			if (right)
			{
				return HitTestResult.HTRIGHT;
			}

			if (top)
			{
				return HitTestResult.HTTOP;
			}

			if (bottom)
			{
				return HitTestResult.HTBOTTOM;
			}

			return HitTestResult.HTCLIENT;
		}
	}
}
