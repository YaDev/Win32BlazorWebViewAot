// DPI handling for a per-monitor-v2 aware window, plus the resize-target math from:
//   ResizePixels      = ceil(ResizeBorderDIP * DPI / 96)
//   EffectiveResizeDIP = max(4, ConfiguredResizeDIP)

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Converts between device-independent pixels (DIP) and physical pixels for a given window DPI.
	/// </summary>
	internal static class WindowDpiHelper
	{
		/// <summary>The DPI a DIP is defined against.</summary>
		public const int BaseDpi = 96;

		/// <summary>the resize hit target may never be smaller than this, however small it is configured.</summary>
		public const int MinimumResizeBorderDip = 4;

		/// <summary>
		/// The default resize hit target in DIP. "minimum 4 DIP" is a floor, not the default: four pixels with
		/// no visible border is nearly impossible to grab by hand (the drag lands on the WebView page instead). Eight DIP
		/// matches what Windows itself uses for a native frame — <c>SM_CXFRAME + SM_CXPADDEDBORDER</c>.
		/// </summary>
		public const int DefaultResizeBorderDip = 8;

		private static bool _awarenessSet;

		/// <summary>
		/// Makes the process per-monitor DPI aware (v2), which is what makes <c>WM_DPICHANGED</c> and
		/// <c>GetDpiForWindow</c> meaningful. Call it once, before any window is created.
		/// </summary>
		/// <remarks>
		/// It is a no-op when the process already declared awareness through its application manifest (the call then
		/// fails with E_ACCESSDENIED, which is the expected outcome rather than an error).
		/// </remarks>
		public static void EnsurePerMonitorDpiAwareness()
		{
			if (_awarenessSet)
			{
				return;
			}

			_awarenessSet = true;
			try
			{
				Native.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
			}
			catch (Exception)
			{
				// Older OS, or awareness already fixed by the manifest. Either way the window still works at system DPI.
			}
		}

		/// <summary>The DPI of <paramref name="hwnd"/>, falling back to 96 when the API is unavailable.</summary>
		public static uint GetWindowDpi(HWND hwnd)
		{
			try
			{
				var dpi = Native.GetDpiForWindow(hwnd);
				return dpi == 0 ? BaseDpi : dpi;
			}
			catch (EntryPointNotFoundException)
			{
				return BaseDpi;
			}
		}

		/// <summary>Converts a DIP length to physical pixels at <paramref name="dpi"/>, rounding up.</summary>
		public static int DipToPixel(int dip, uint dpi) => (int)Math.Ceiling(dip * (double)dpi / BaseDpi);

		/// <summary>Converts a DIP length to physical pixels at <paramref name="dpi"/>, rounding to nearest.</summary>
		public static double DipToPixel(double dip, uint dpi) => dip * dpi / BaseDpi;

		/// <summary>Converts physical pixels to DIP at <paramref name="dpi"/>.</summary>
		public static double PixelToDip(int pixels, uint dpi) => pixels * (double)BaseDpi / dpi;

		/// <summary>
		/// The resize border to use for hit-testing: never below <see cref="MinimumResizeBorderDip"/> DIP (
		/// floor), converted to whole pixels by rounding up so the target stays reachable at high DPI.
		/// </summary>
		public static int GetResizeBorderPixels(int configuredResizeBorderDip, uint dpi)
		{
			var effectiveDip = Math.Max(MinimumResizeBorderDip, configuredResizeBorderDip);
			return DipToPixel(effectiveDip, dpi);
		}
	}
}
