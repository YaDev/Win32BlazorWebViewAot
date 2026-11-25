// Blazor's Dispatcher over the Win32 message queue: inline on the UI thread, scheduled from other threads, and a completed
// queue rejects everything — including UI-thread dispatches — instead of running against a teardown in progress.
// The rules it upholds and why: "Threading" in doc/DESIGN.md.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Runs Blazor work on a Win32 UI thread.
	/// </summary>
	internal sealed class Win32Dispatcher(Win32MessageQueue queue, Action<Exception> onUnhandledException) : Dispatcher
	{
		private readonly Win32MessageQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
		private readonly Action<Exception> _onUnhandledException = onUnhandledException ?? throw new ArgumentNullException(nameof(onUnhandledException));

		/// <inheritdoc />
		public override bool CheckAccess() => _queue.CheckAccess();

		/// <inheritdoc />
		public override async Task InvokeAsync(Action workItem)
		{
			ArgumentNullException.ThrowIfNull(workItem);

			// Rejection lives outside the try: a closed queue is expected during teardown and faults the caller's task,
			// but it is not a work failure, so it must not reach the unhandled-exception path.
			ThrowIfQueueClosed();
			try
			{
				if (CheckAccess())
				{
					workItem();
				}
				else
				{
					await _queue.InvokeAsync(workItem).ConfigureAwait(false);
				}
			}
			catch (Exception exception)
			{
				Report(exception);
				throw;
			}
		}

		/// <inheritdoc />
		public override async Task InvokeAsync(Func<Task> workItem)
		{
			ArgumentNullException.ThrowIfNull(workItem);

			ThrowIfQueueClosed();
			try
			{
				if (CheckAccess())
				{
					await workItem().ConfigureAwait(false);
				}
				else
				{
					await _queue.InvokeAsync(() => workItem()).Unwrap().ConfigureAwait(false);
				}
			}
			catch (Exception exception)
			{
				Report(exception);
				throw;
			}
		}

		/// <inheritdoc />
		public override async Task<TResult> InvokeAsync<TResult>(Func<TResult> workItem)
		{
			ArgumentNullException.ThrowIfNull(workItem);

			ThrowIfQueueClosed();
			try
			{
				return CheckAccess() ? workItem() : await _queue.InvokeAsync(workItem).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				Report(exception);
				throw;
			}
		}

		/// <inheritdoc />
		public override async Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> workItem)
		{
			ArgumentNullException.ThrowIfNull(workItem);

			ThrowIfQueueClosed();
			try
			{
				return CheckAccess()
					? await workItem().ConfigureAwait(false)
					: await _queue.InvokeAsync(() => workItem()).Unwrap().ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				Report(exception);
				throw;
			}
		}

		private void ThrowIfQueueClosed()
		{
			if (_queue.IsComplete)
			{
				throw new Win32DispatcherException("The UI thread is no longer processing work.");
			}
		}

		private void Report(Exception exception)
		{
			// Two upstream failures are teardown races, not application bugs: the WebView is already closed, and the
			// renderer no longer knows the component that threw. Both still fault the caller's task below.
			if (exception is ObjectDisposedException || IsRendererDisposedRace(exception))
			{
				return;
			}

			if (CheckAccess())
			{
				_onUnhandledException(exception);
				return;
			}

			_ = _queue.TryPost(() => _onUnhandledException(exception));
		}

		private static bool IsRendererDisposedRace(Exception exception) =>
			exception is ArgumentException argumentException &&
			argumentException.Message.StartsWith("The renderer does not have a component with ID", StringComparison.Ordinal);
	}
}
