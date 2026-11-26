// Blazor's Dispatcher over the Win32 message queue: inline on the UI thread, scheduled from other threads, and a completed
// queue rejects everything — including UI-thread dispatches — instead of running against a teardown in progress.
//
// A failure here belongs to whoever asked for the work: it faults their task and stops. Reporting is reserved for failures that
// have no awaiter — the work this library deliberately drops, and anything run through the queue's posted callbacks — because
// reporting something the caller already caught turns a handled bug into an unhandled report.
// The rules it upholds and why: "Threading" in doc/DESIGN.md.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Runs Blazor work on a Win32 UI thread. A failure faults the awaiting caller's task; only work nobody awaits is
	/// reported through the view's unhandled path.
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
			catch
			{
				// The caller awaits this task and sees the failure. Reporting it as well would double-report every
				// handled bug, and promoting one nobody marks handled would report a failure that was never unhandled.
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
			catch
			{
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
			catch
			{
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
			catch
			{
				throw;
			}
		}

		/// <summary>
		/// Watches work whose task the caller drops on purpose. Nobody can await it, so a failure would vanish into the finalizer
		/// queue; this is what keeps the unhandled path meaningful now that awaited failures report nothing.
		/// </summary>
		internal void ObserveUnawaited(Task work)
		{
			ArgumentNullException.ThrowIfNull(work);

			if (work.IsFaulted)
			{
				// Failed before the caller could hand it over. Reading Exception also marks it observed, which is correct:
				// it has been reported, so the runtime should not raise it a second time at collection.
				ReportFailure(work.Exception!);
				return;
			}

			if (work.IsCompletedSuccessfully)
			{
				return;
			}

			_ = work.ContinueWith(
				static (faulted, state) => ((Win32Dispatcher)state!).ReportFailure(faulted.Exception!),
				this,
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}

		private void ReportFailure(AggregateException exception)
		{
			var baseException = exception.GetBaseException();

			// A queue that completed mid-work is teardown, not a bug: the same case ThrowIfQueueClosed treats as expected.
			if (baseException is Win32DispatcherException)
			{
				return;
			}

			Report(baseException);
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
