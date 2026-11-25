// Resumes asynchronous work on a Win32 UI thread. WebView2's COM objects are thread-affine, so an await in
// startup or component code has to come back to the queue that owns them.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// A <see cref="SynchronizationContext"/> backed by a <see cref="Win32MessageQueue"/>.
	/// </summary>
	public sealed class Win32SynchronizationContext(Win32MessageQueue queue) : SynchronizationContext
	{
		private readonly Win32MessageQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));

		/// <summary>
		/// Queues the callback. Posting never throws: once the queue is closed the callback is dropped, which matches
		/// a thread that has left its message loop.
		/// </summary>
		public override void Post(SendOrPostCallback callback, object? state) => _ = _queue.TryPost(() => callback(state));

		/// <summary>
		/// Runs the callback on the UI thread and blocks until it finished. Never call it from the UI thread with work
		/// that re-enters the pump; it exists for components and libraries that require synchronous dispatch.
		/// </summary>
		public override void Send(SendOrPostCallback callback, object? state)
		{
			if (_queue.CheckAccess())
			{
				callback(state);
				return;
			}

			_queue.InvokeAsync(() => callback(state)).GetAwaiter().GetResult();
		}

		/// <inheritdoc />
		public override SynchronizationContext CreateCopy() => this;
	}
}
