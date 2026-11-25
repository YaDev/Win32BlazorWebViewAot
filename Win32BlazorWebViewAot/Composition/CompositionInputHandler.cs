// Translates Win32 mouse messages into the WebView2 composition input contract.
// A composition-hosted WebView receives no OS input at all: every click, move and wheel has to be re-delivered here.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Maps Win32 mouse messages and key-state flags onto the values <c>SendMouseInput</c> expects.
	/// </summary>
	internal static class CompositionInputHandler
	{
		// Win32 MK_* flags. WebView2's COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS uses the same numeric layout
		// (left 1, right 2, shift 4, control 8, middle 16, x1 32, x2 64), which is why TranslateKeyStates can be a
		// masked copy rather than a switch — but it masks explicitly so a future divergence shows up here.
		private const uint MkLButton = 0x0001;
		private const uint MkRButton = 0x0002;
		private const uint MkShift = 0x0004;
		private const uint MkControl = 0x0008;
		private const uint MkMButton = 0x0010;
		private const uint MkXButton1 = 0x0020;
		private const uint MkXButton2 = 0x0040;

		/// <summary>
		/// The WebView2 event kind for a client-area mouse message, or <see langword="null"/> when the message is not one
		/// this host forwards (non-client messages belong to Windows, not to the browser).
		/// </summary>
		public static COREWEBVIEW2_MOUSE_EVENT_KIND? ButtonKind(uint message) => message switch
		{
			MessageDecoder.WM_LBUTTONDOWN => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_DOWN,
			MessageDecoder.WM_LBUTTONUP => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_UP,
			MessageDecoder.WM_LBUTTONDBLCLK => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_LEFT_BUTTON_DOUBLE_CLICK,
			MessageDecoder.WM_RBUTTONDOWN => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_DOWN,
			MessageDecoder.WM_RBUTTONUP => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_UP,
			MessageDecoder.WM_RBUTTONDBLCLK => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_RIGHT_BUTTON_DOUBLE_CLICK,
			MessageDecoder.WM_MBUTTONDOWN => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_MIDDLE_BUTTON_DOWN,
			MessageDecoder.WM_MBUTTONUP => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_MIDDLE_BUTTON_UP,
			MessageDecoder.WM_MBUTTONDBLCLK => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_MIDDLE_BUTTON_DOUBLE_CLICK,
			MessageDecoder.WM_XBUTTONDOWN => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_X_BUTTON_DOWN,
			MessageDecoder.WM_XBUTTONUP => COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_X_BUTTON_UP,
			_ => null,
		};

		/// <summary>The event kind for a wheel message.</summary>
		public static COREWEBVIEW2_MOUSE_EVENT_KIND WheelKind(bool isHorizontal) => isHorizontal
			? COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_HORIZONTAL_WHEEL
			: COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_WHEEL;

		/// <summary>The event kind reporting that the pointer left the window.</summary>
		public static COREWEBVIEW2_MOUSE_EVENT_KIND LeaveKind =>
			COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_LEAVE;

		/// <summary>The event kind for a move.</summary>
		public static COREWEBVIEW2_MOUSE_EVENT_KIND MoveKind =>
			COREWEBVIEW2_MOUSE_EVENT_KIND.COREWEBVIEW2_MOUSE_EVENT_KIND_MOVE;

		/// <summary>
		/// Converts the Win32 <c>MK_*</c> key states of a mouse message into WebView2 virtual keys, so the browser sees
		/// which buttons were held and which modifiers were down (drag, multi-select, context menus).
		/// </summary>
		public static COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS TranslateKeyStates(uint keyStates)
		{
			var keys = COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_NONE;

			if ((keyStates & MkLButton) != 0)
			{
				keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_LEFT_BUTTON;
			}

			if ((keyStates & MkRButton) != 0)
			{
				keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_RIGHT_BUTTON;
			}

			if ((keyStates & MkShift) != 0)
			{
				keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_SHIFT;
			}

			if ((keyStates & MkControl) != 0)
			{
				keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_CONTROL;
			}

			if ((keyStates & MkMButton) != 0)
			{
				keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_MIDDLE_BUTTON;
			}

			if ((keyStates & MkXButton1) != 0)
			{
				keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_X_BUTTON1;
			}

			if ((keyStates & MkXButton2) != 0)
			{
				keys |= COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS.COREWEBVIEW2_MOUSE_EVENT_VIRTUAL_KEYS_X_BUTTON2;
			}

			return keys;
		}
	}
}
