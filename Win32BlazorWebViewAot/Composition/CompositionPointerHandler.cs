// Touch and pen input for a composition-hosted WebView: map the message to an event kind, build the POINTER_INFO for the
// pointer id, hand it over. When the runtime cannot build one, pointer events stop rather than throw; mouse keeps working.
// Why it is shaped this way: "Input and drop under composition hosting" in doc/DESIGN.md.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Forwards <c>WM_POINTER*</c> messages to the composition controller, falling back to mouse-only input when the
	/// installed WebView2 runtime cannot describe a pointer.
	/// </summary>
	/// <remarks>
	/// The fallback is decided once, on the first pointer message, not per message: probing an interface that does not
	/// exist costs a failed QueryInterface every time, and touch input arrives at hundreds of messages a second.
	/// </remarks>
	internal sealed class CompositionPointerHandler(IComObject<ICoreWebView2CompositionController> controller)
	{
		private readonly IComObject<ICoreWebView2CompositionController> _controller = controller;

		// Null = not probed yet, false = runtime cannot do pointer input, true = working.
		private bool? _isSupported;

		/// <summary>Whether the runtime accepted pointer forwarding (false once it has refused).</summary>
		public bool IsSupported => _isSupported != false;

		/// <summary>
		/// Forwards one pointer message. Coordinates are read from the system pointer info rather than packed in the
		/// message, which is why only the id is needed here.
		/// </summary>
		/// <param name="message">One of <c>WM_POINTERDOWN/UPDATE/UP/ENTER/LEAVE</c>.</param>
		/// <param name="pointerId">The pointer id from <c>wParam</c>.</param>
		/// <param name="parentWindow">The window that received the message; the browser keeps it as the pointer's origin.</param>
		public void Send(uint message, uint pointerId, HWND parentWindow)
		{
			if (_isSupported == false)
			{
				return;
			}

			if (!TryGetEventKind(message, out var eventKind))
			{
				return;
			}

			try
			{
				// Identity transform: the pointer coordinates the runtime reports are already in this window's pixels.
				using var pointerInfo = _controller.CreateCoreWebView2PointerInfoFromPointerId(pointerId, parentWindow, Identity);

				if (pointerInfo is null)
				{
					_isSupported = false;
					return;
				}

				var result = _controller.SendPointerInput(eventKind, pointerInfo, throwOnError: false);
				if (result.Value == NoInterface || result.Value == NotImplemented)
				{
					// A runtime that creates the info but cannot send it is as unusable as one that cannot create it.
					_isSupported = false;
					return;
				}

				_isSupported = true;
			}
			catch (Exception)
			{
				// First failure disables pointer forwarding for the life of the view: a browser that rejects pointers on
				// message 1 will reject them on message 1000, and mouse input still works.
				_isSupported = false;
			}
		}

		private const int NoInterface = unchecked((int)0x80004002);
		private const int NotImplemented = unchecked((int)0x80004001);

		// Identity: the pointer coordinates the runtime reports are already in this window's pixels, so nothing is mapped.
		private static D2D_MATRIX_4X4_F Identity => D2D_MATRIX_4X4_F.Identity();

		private static bool TryGetEventKind(uint message, out COREWEBVIEW2_POINTER_EVENT_KIND eventKind)
		{
			eventKind = message switch
			{
				MessageDecoder.WM_POINTERDOWN => COREWEBVIEW2_POINTER_EVENT_KIND.COREWEBVIEW2_POINTER_EVENT_KIND_DOWN,
				MessageDecoder.WM_POINTERUPDATE => COREWEBVIEW2_POINTER_EVENT_KIND.COREWEBVIEW2_POINTER_EVENT_KIND_UPDATE,
				MessageDecoder.WM_POINTERUP => COREWEBVIEW2_POINTER_EVENT_KIND.COREWEBVIEW2_POINTER_EVENT_KIND_UP,
				MessageDecoder.WM_POINTERENTER => COREWEBVIEW2_POINTER_EVENT_KIND.COREWEBVIEW2_POINTER_EVENT_KIND_ENTER,
				MessageDecoder.WM_POINTERLEAVE => COREWEBVIEW2_POINTER_EVENT_KIND.COREWEBVIEW2_POINTER_EVENT_KIND_LEAVE,
				_ => default,
			};

			// WM_POINTERROUTED* and activation messages belong to the host, not to the page.
			return message is MessageDecoder.WM_POINTERDOWN
				or MessageDecoder.WM_POINTERUPDATE
				or MessageDecoder.WM_POINTERUP
				or MessageDecoder.WM_POINTERENTER
				or MessageDecoder.WM_POINTERLEAVE;
		}
	}
}
