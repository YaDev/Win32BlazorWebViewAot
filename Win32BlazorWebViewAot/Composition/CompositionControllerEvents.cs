// Cursor synchronization for a composition-hosted WebView.
//
// The browser decides what the cursor should be (text, link, wait...), but with composition hosting it has no window
// of its own to apply it to: it raises CursorChanged and the host must call SetCursor itself.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Subscribes to the composition controller's events and republishes the few this host acts on.
	/// </summary>
	/// <remarks>
	/// The underlying <see cref="CoreWebView2CompositionControllerEvents"/> object holds native event registrations, so
	/// it lives exactly as long as the controller and is disposed explicitly — dropping it silently would leave the
	/// browser raising events into a collected managed handler.
	/// </remarks>
	internal sealed class CompositionControllerEvents : IDisposable
	{
		private readonly CoreWebView2CompositionControllerEvents _events;
		private bool _isDisposed;

		public CompositionControllerEvents(IComObject<ICoreWebView2CompositionController> controller)
		{
			ArgumentNullException.ThrowIfNull(controller);
			_events = new CoreWebView2CompositionControllerEvents(controller);
			_events.CursorChanged += OnCursorChanged;
		}

		/// <summary>Raised when the browser wants a different cursor. The argument is the native cursor handle.</summary>
		public event EventHandler<HCURSOR>? CursorChanged;

		private void OnCursorChanged(object? sender, EventArgs eventArgs)
		{
			if (_isDisposed)
			{
				return;
			}

			try
			{
				// The handler's sender is the controller that raised; read its cursor through the same wrapper so no
				// second COM identity is created for one controller.
				if (sender is ICoreWebView2CompositionController controller)
				{
					CursorChanged?.Invoke(this, controller.Cursor);
				}
			}
			catch (Exception)
			{
				// A cursor change racing browser teardown is not worth losing the message loop over.
			}
		}

		public void Dispose()
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;
			_events.CursorChanged -= OnCursorChanged;
			_events.Dispose();
		}
	}
}
