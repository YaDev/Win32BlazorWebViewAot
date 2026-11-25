// A thread-affine work queue for a Win32 UI thread.
//
// Work posted from any thread lands in a concurrent queue and is drained on the UI thread by a message-only window, so
// continuations of WebView2 work resume where COM says they must. One queue per UI thread: several windows on the same
// thread share it rather than each installing its own synchronization context.

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Schedules work onto a Win32 UI thread using <c>PostMessage</c>/<c>WM_APP</c>.
	/// </summary>
	public sealed class Win32MessageQueue : IDisposable
	{
		private const uint QueueMessage = ExtraWindowMessages.WM_APP;

		[ThreadStatic]
		private static Win32MessageQueue? t_current;

		private static readonly Lock s_gate = new();
		private static readonly List<Win32MessageQueue> s_all = new();

		private readonly ConcurrentQueue<Action> _work = new();
		private readonly WNDPROC _wndProc;
		private readonly nint _classAtom;
		private readonly string _className;
		private nint _handle;
		private volatile bool _complete;

		/// <summary>Creates and starts a queue for the current thread.</summary>
		/// <exception cref="InvalidOperationException">The current thread is not STA, or already owns a queue.</exception>
		public Win32MessageQueue()
		{
			if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
			{
				throw new InvalidOperationException("A message queue must be created on an STA thread.");
			}

			if (t_current is not null)
			{
				throw new InvalidOperationException("This thread already owns a Win32MessageQueue. Reuse it with GetOrCreateForCurrentThread().");
			}

			_className = $"Win32BlazorWebViewAot.Queue.{Environment.CurrentManagedThreadId}";
			_wndProc = new WNDPROC(WndProc);
			_classAtom = RegisterClass();

			var instance = (nint)Native.GetModuleHandleW(default);
			var className = NativeText.From(_className);
			var windowName = NativeText.From("Win32BlazorWebViewAot queue");
			try
			{
				// A message-only window: no parent, no styles, just a target for PostMessage that never reaches the screen.
				_handle = Native.CreateWindowExW(
					0,
					className.Value,
					windowName.Value,
					0,
					0,
					0,
					0,
					0,
					HWND.MESSAGE,
					default,
					(HINSTANCE)instance,
					IntPtr.Zero).Value;
			}
			finally
			{
				className.Dispose();
				windowName.Dispose();
			}

			if (_handle == 0)
			{
				throw new InvalidOperationException($"Cannot create the message-only window: Win32 error {Marshal.GetLastWin32Error()}.");
			}

			SynchronizationContext = new Win32SynchronizationContext(this);
			t_current = this;

			lock (s_gate)
			{
				s_all.Add(this);
			}
		}

		private nint RegisterClass()
		{
			var instance = (nint)Native.GetModuleHandleW(default);
			var className = NativeText.From(_className);
			var windowClass = new WNDCLASSW
			{
				style = 0,
				lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
				hInstance = (HINSTANCE)instance,
				lpszClassName = className.Value,
			};

			var atom = Native.RegisterClassW(in windowClass);

			// Registration copies the class name, so the buffer is ours to release immediately.
			className.Dispose();
			return atom;
		}

		/// <summary>The queue owned by the current thread, creating one when the thread has none.</summary>
		public static Win32MessageQueue GetOrCreateForCurrentThread() => t_current ?? new Win32MessageQueue();

		/// <summary>The queue of the current thread, or <see langword="null"/>.</summary>
		public static Win32MessageQueue? Current => t_current;

		/// <summary>The synchronization context that resumes async work on this queue's thread.</summary>
		public Win32SynchronizationContext SynchronizationContext { get; }

		/// <summary>The thread this queue drains work on.</summary>
		public int ThreadId { get; } = Environment.CurrentManagedThreadId;

		/// <summary>Raised when a queued callback throws and nothing else observed it.</summary>
		public event EventHandler<Win32UnhandledExceptionEventArgs>? UnhandledException;

		/// <summary><see langword="true"/> once the queue stopped accepting work.</summary>
		public bool IsComplete => _complete;

		/// <summary><see langword="true"/> when the calling thread is the queue's thread.</summary>
		public bool CheckAccess() => ThreadId == Environment.CurrentManagedThreadId;

		/// <summary>
		/// Queues work without waiting.
		/// </summary>
		/// <returns><see langword="false"/> when the queue is closed and the work will never run.</returns>
		public bool TryPost(Action callback)
		{
			ArgumentNullException.ThrowIfNull(callback);

			if (_complete)
			{
				return false;
			}

			// Capture so the caller's ExecutionContext (culture, Activity, logical call context) survives the hop.
			var context = ExecutionContext.Capture();
			_work.Enqueue(() => Execute(callback, context));

			if (!_complete && !Native.PostMessageW(new HWND { Value = _handle }, QueueMessage, default, default))
			{
				// The window is gone; drain nothing and report the drop instead of losing the work silently.
				return false;
			}

			return true;
		}

		/// <summary>Queues work and returns a task that completes when it ran (or faults when it throws).</summary>
		public Task InvokeAsync(Action callback) => Schedule(callback).Task;

		/// <inheritdoc cref="InvokeAsync(Action)"/>
		public Task<TResult> InvokeAsync<TResult>(Func<TResult> callback)
		{
			ArgumentNullException.ThrowIfNull(callback);

			var source = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (!TryPost(() =>
			{
				try
				{
					source.TrySetResult(callback());
				}
				catch (Exception exception)
				{
					source.TrySetException(exception);
				}
			}))
			{
				source.TrySetException(new Win32DispatcherException("The UI thread is no longer processing work."));
			}

			return source.Task;
		}

		/// <summary>
		/// Stops accepting work. Work already queued still drains, so pending component continuations finish.
		/// </summary>
		public void Complete() => _complete = true;

		/// <inheritdoc />
		public void Dispose()
		{
			lock (s_gate)
			{
				_ = s_all.Remove(this);
			}

			Complete();

			if (_handle != 0)
			{
				Native.DestroyWindow(new HWND { Value = _handle });
				_handle = 0;
			}

			if (ReferenceEquals(t_current, this))
			{
				t_current = null;
			}
		}

		private TaskCompletionSource<object?> Schedule(Action callback)
		{
			var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

			if (!TryPost(() =>
			{
				try
				{
					callback();
					source.TrySetResult(null);
				}
				catch (Exception exception)
				{
					source.TrySetException(exception);
				}
			}))
			{
				source.TrySetException(new Win32DispatcherException("The UI thread is no longer processing work."));
			}

			return source;
		}

		private void Execute(Action callback, ExecutionContext? context)
		{
			if (context is null)
			{
				callback();
				return;
			}

			ExecutionContext.Run(context, static state => ((Action)state!)(), callback);
		}

		private LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
		{
			if (message != QueueMessage)
			{
				return Native.DefWindowProcW(hwnd, message, wParam, lParam);
			}

			try
			{
				// Drain everything that is ready. A callback that posts more work gets the next turn through the pump, so a
				// badly behaved component cannot starve input messages.
				while (_work.TryDequeue(out var action))
				{
					try
					{
						action();
					}
					catch (Exception exception)
					{
						Report(exception);
					}
				}
			}
			catch (Exception exception)
			{
				// Reporting is not infallible: Report calls subscribers, and a subscriber that fails is called from inside this
				// window procedure. Its frames above are native user32 ones, so the throw would fail the process; it goes to the
				// same boundary an application's own handler failure gets.
				Win32Window.ReportHandlerFailure(exception);
			}

			return 0;
		}

		private void Report(Exception exception)
		{
			var args = new Win32UnhandledExceptionEventArgs(exception);
			UnhandledException?.Invoke(this, args);

			if (!args.Handled)
			{
				RethrowAsUnhandled(exception);
			}
		}

		/// <summary>
		/// Promotes an exception to the runtime's unhandled-exception path (so a debugger or the host's crash handler sees
		/// it) instead of letting it disappear with the message loop.
		/// </summary>
		public static void RethrowAsUnhandled(Exception exception) =>
			Task.Factory.StartNew(static state => ExceptionDispatchInfo.Capture((Exception)state!).Throw(),
				exception, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
	}

	/// <summary>Carries an exception raised by queued work and lets a handler mark it handled.</summary>
	public sealed class Win32UnhandledExceptionEventArgs(Exception exception) : EventArgs
	{
		/// <summary>The exception thrown by the callback.</summary>
		public Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));

		/// <summary>Set it to keep the exception from being promoted to an unhandled exception.</summary>
		public bool Handled { get; set; }
	}
}
