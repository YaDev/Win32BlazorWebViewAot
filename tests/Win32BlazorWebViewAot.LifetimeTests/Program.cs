using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LifetimeTests.Components;
using Win32BlazorWebViewAot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Functions = DirectN.Functions;
using HWND = DirectN.HWND;
using POINT = DirectN.POINT;
using PEEK_MESSAGE_REMOVE_TYPE = DirectN.PEEK_MESSAGE_REMOVE_TYPE;
using SET_WINDOW_POS_FLAGS = DirectN.SET_WINDOW_POS_FLAGS;

namespace Win32BlazorWebViewAot.LifetimeTests;

/// <summary>
/// The lifetime harness asks for: the ten shutdown scenarios, driven against real windows and a real WebView2
/// runtime. A scenario fails when it throws, when an exception reaches a view's unhandled-exception handler (that means
/// something escaped, which is what would crash a real app), or when work that must finish never finishes.
/// </summary>
/// <remarks>
/// Usage: <c>Win32BlazorWebViewAot.LifetimeTests [delayMs] [--hold ms] [--only substring] [--verbose]</c>. Exit code 0 = all pass.
/// The scenarios run on one STA thread and pump Win32 messages themselves (<see cref="Pump"/>), because the library's own
/// callbacks — WebView2 completion, queued work — are delivered by that loop. Nothing here may block the thread.
/// </remarks>
internal static class Program
{
	private static readonly ConcurrentQueue<string> s_fatal = new();
	private static readonly ConcurrentQueue<string> s_unobserved = new();
	private static readonly ConcurrentQueue<string> s_logged = new();
	private static readonly ConcurrentQueue<string> s_urlLoading = new();
	private static readonly List<string> s_notes = [];

	private static bool s_verbose;
	private static TimeSpan s_hold = TimeSpan.FromMilliseconds(1200);

	[STAThread]
	private static int Main(string[] args)
	{
		var delay = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 3000;
		s_verbose = Array.IndexOf(args, "--verbose") >= 0;
		var holdIndex = Array.IndexOf(args, "--hold");
		if (holdIndex >= 0 && holdIndex + 1 < args.Length && int.TryParse(args[holdIndex + 1], out var hold))
		{
			s_hold = TimeSpan.FromMilliseconds(hold);
		}

		var only = GetOptionValue(args, "--only");

		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			// The process dies after this handler runs, so print immediately rather than queueing.
			Console.WriteLine($"FATAL AppDomain.UnhandledException: {e.ExceptionObject}");
			Console.Out.Flush();
		};
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			s_unobserved.Enqueue(e.Exception.GetBaseException().Message);
			e.SetObserved();
		};

		var scenarios = new (string Name, Action<IServiceProvider, int> Run)[]
		{
			("1a Close window while a component runs async work", (sp, ms) => CloseDuringAsyncWork(sp, ms, throwException: false)),
			("1b Close window while a component runs async work that fails", (sp, ms) => CloseDuringAsyncWork(sp, ms, throwException: true)),
			("2  Close window while a component awaits JavaScript interop", (sp, ms) => CloseDuringJsInterop(sp, ms)),
			("3  Close window while WebView2 is still initializing", (sp, ms) => CloseDuringInitialization(sp, ms)),
			("4  Close window during initial navigation", (sp, ms) => CloseDuringNavigation(sp, ms)),
			("5  Dispose the view before the HWND is destroyed", (sp, ms) => DisposeViewBeforeWindow(sp, ms)),
			("6  Dispose twice (view and window)", (sp, ms) => DisposeTwice(sp, ms)),
			("7  Close multiple windows on one UI thread", (sp, ms) => CloseMultipleWindows(sp, ms, count: 4)),
			("8  Close windows that share one WebView2 environment", (sp, ms) => CloseSharedEnvironment(ms)),
			("9  Simulate a WebView2 browser process crash", (sp, ms) => BrowserProcessCrash(sp, ms)),
			("10a Dispatcher work issued during shutdown", (sp, ms) => DispatcherWorkDuringShutdown(sp, ms)),
			("11 Real mouse click reaches the Blazor page", (sp, ms) => ClickReachesPage(sp, ms)),
			("12 Dragging the window edge resizes it", (sp, ms) => EdgeDragResizesWindow(sp, ms)),
			("13 Dragging the caption strip moves the window", (sp, ms) => CaptionDragMovesWindow(sp, ms)),
			("14 Native title bar window resizes from its frame", (sp, ms) => NativeFrameDragResizesWindow(sp, ms)),
			("15 A message handler that throws does not kill the process", (sp, ms) => ThrowingMessageHandlerIsReported(sp, ms)),
			("16 A queue subscriber that throws does not kill the process", (sp, ms) => ThrowingQueueSubscriberIsReported(sp, ms)),
			("17 Marking a reported failure handled keeps it out of the unhandled path", (sp, ms) => HandledFlagControlsPromotion(sp, ms)),
			("18 A new-window request for a non-browser scheme is refused, not shell-executed", (sp, ms) => NewWindowSchemeAllowList(sp, ms)),
			("19 A queue whose window class name was taken still runs its own work", (sp, ms) => QueueOnRecycledThreadIdRunsWork(sp, ms)),
			// 10b MUST stay last: it completes THIS thread's Win32MessageQueue, which is terminal — every later scenario
			// would fault its dispatcher work with Win32DispatcherException (observed when 11 ran after it).
			("10b Work posted to a closed queue faults instead of hanging", (sp, ms) => WorkOnClosedQueue(sp, ms)),
		};

		if (only is not null)
		{
			scenarios = [.. scenarios.Where(s => s.Name.Contains(only, StringComparison.OrdinalIgnoreCase))];
			if (scenarios.Length == 0)
			{
				Console.WriteLine($"No scenario matches '{only}'.");
				return 2;
			}
		}

		var results = new List<(string Name, string Outcome, string Detail)>();
		foreach (var (name, run) in scenarios)
		{
			Console.WriteLine($"--- {name}");

			s_fatal.Clear();
			s_unobserved.Clear();
			s_logged.Clear();
			s_urlLoading.Clear();
			s_notes.Clear();
			BackgroundWorkState.Reset();

			var stopwatch = Stopwatch.StartNew();
			string? error = null;
			try
			{
				run(CreateServices(), delay);
			}
			catch (Exception exception)
			{
				error = $"{exception.GetType().Name}: {exception.Message}";
				if (s_verbose)
				{
					Console.WriteLine(exception);
				}
			}

			// Give late continuations and finalizers a chance to surface what they would have thrown.
			Pump.For(TimeSpan.FromMilliseconds(500));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Pump.For(TimeSpan.FromMilliseconds(200));

			// A view reporting an unhandled exception is a failure: in an app that is the crash path.
			var failures = new List<string>();
			if (error is not null)
			{
				failures.Add(error);
			}

			failures.AddRange(s_fatal);

			// Not failures, but worth printing: the component's own fire-and-forget task observing a disposed renderer, and
			// the error boundary logging the exception the scenario threw on purpose.
			var info = new List<string>(s_notes);
			if (!s_unobserved.IsEmpty)
			{
				info.Add($"unobserved task exceptions: {string.Join(" | ", s_unobserved)}");
			}

			if (!s_logged.IsEmpty)
			{
				info.Add($"error logs: {string.Join(" | ", s_logged)}");
			}

			var outcome = failures.Count == 0 ? "PASS" : "FAIL";
			var detail = string.Join("; ", failures);
			if (info.Count > 0)
			{
				detail = (detail.Length > 0 ? detail + "; " : "") + string.Join("; ", info);
			}

			results.Add((name, outcome, detail));
			Console.WriteLine(
				$"    {outcome} in {stopwatch.Elapsed.TotalSeconds:N1}s " +
				$"(work started {BackgroundWorkState.Started}, finished {BackgroundWorkState.Finished}" +
				$"{(BackgroundWorkState.LastFailure is { } failure ? $"; component failure kept in-component: {failure}" : "")})");
		}

		Console.WriteLine();
		Console.WriteLine("==== Summary");
		foreach (var (name, outcome, detail) in results)
		{
			Console.WriteLine($"{outcome}  {name}{(detail.Length > 0 ? $"\n      {detail}" : "")}");
		}

		return results.TrueForAll(r => r.Outcome == "PASS") ? 0 : 1;
	}

	// the supported shutdown path, with work still running. The window's WM_CLOSE teardown disposes the view;
	// the component's task must still reach its end afterwards instead of being abandoned mid-await.
	private static void CloseDuringAsyncWork(IServiceProvider services, int delay, bool throwException)
	{
		var window = OpenWindow(services, delay, throwException);

		WaitFor(() => BackgroundWorkState.Started >= 1, TimeSpan.FromSeconds(60), "background work to start");
		Pump.For(s_hold);

		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "window to close");

		// The task outlives the view: it finishes (or faults) on the thread pool, and nothing about that may reach the host.
		WaitFor(() => BackgroundWorkState.Finished >= 1, FromDelay(delay, 30), "background work to finish");
	}

	// closing while a call into JavaScript is in flight. The reply can never arrive — the renderer is gone —
	// so the awaiting component must be faulted by Blazor rather than left pending forever.
	private static void CloseDuringJsInterop(IServiceProvider services, int delay)
	{
		var window = OpenWindow(services, delay, awaitJsInterop: true);

		WaitFor(() => BackgroundWorkState.JsStarted >= 1, TimeSpan.FromSeconds(60), "the JS interop call to start");
		Pump.For(TimeSpan.FromMilliseconds(300));

		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "window to close");

		// Pending interop is released by renderer disposal: the component reaches its finally block. If this times out with
		// JsFinished still 0, the await was orphaned — a real leak, not a slow machine.
		WaitFor(() => BackgroundWorkState.Finished >= 1, FromDelay(delay, 30), "the awaited interop to be released");

		if (BackgroundWorkState.JsFinished >= 1)
		{
			// Not a failure — the page answered before teardown. Recorded so the run says which case actually happened.
			s_notes.Add("the JS promise resolved before disposal; interop was not in flight at close");
		}
	}

	// WM_CLOSE lands while CreateCoreWebView2CompositionControllerAsync is still pending. The startup task
	// must end (completed or faulted) and the window must go away without an exception escaping from a half-built browser.
	private static void CloseDuringInitialization(IServiceProvider services, int delay)
	{
		var window = new BlazorWebViewWindow("Win32BlazorWebViewAot.LifetimeTests") { QuitOnDestroy = false };
		Configure(window.View, services, delay);

		window.Show();

		// No pumping in between: the close is posted before any initialization callback can be delivered.
		var start = window.View.StartAsync();
		window.Close();

		WaitFor(() => start.IsCompleted, TimeSpan.FromSeconds(60), "startup to settle");
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "window to close");

		if (start.IsFaulted)
		{
			// A browser that was cancelled mid-creation is expected; it must not have taken the process down. Report which
			// way it went so a change in behaviour is visible rather than silently accepted.
			s_notes.Add($"startup faulted as {start.Exception!.GetBaseException().GetType().Name}");
		}

		if (!window.View.IsDisposed)
		{
			throw new InvalidOperationException("The window closed but its view was never disposed.");
		}
	}

	// the browser exists and the first Navigate has been issued, but the page (and Blazor's own startup) is
	// still loading when the user closes the window.
	private static void CloseDuringNavigation(IServiceProvider services, int delay)
	{
		var window = new BlazorWebViewWindow("Win32BlazorWebViewAot.LifetimeTests") { QuitOnDestroy = false };

		using var initialized = new ManualResetEventSlim();
		window.View.BlazorWebViewInitialized += (_, _) => initialized.Set();

		Configure(window.View, services, delay);
		window.Show();

		var start = window.View.StartAsync();
		WaitFor(() => start.IsCompleted, TimeSpan.FromSeconds(60), "startup to settle");
		if (start.IsFaulted)
		{
			throw start.Exception!.GetBaseException();
		}

		// Initialized means the controller and IPC bridge exist; the host page is loading right now.
		if (!initialized.Wait(TimeSpan.FromSeconds(60)))
		{
			throw new TimeoutException("Timed out waiting for BlazorWebViewInitialized.");
		}

		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "window to close during navigation");
	}

	// the documented order — dispose the view while the HWND is still alive (component disposal needs a live
	// browser and a pumping loop), then destroy the window.
	private static void DisposeViewBeforeWindow(IServiceProvider services, int delay)
	{
		var window = OpenWindow(services, delay);

		WaitFor(() => BackgroundWorkState.Started >= 1, TimeSpan.FromSeconds(60), "background work to start");
		Pump.For(s_hold);

		var dispose = window.View.DisposeAsync().AsTask();
		WaitFor(() => dispose.IsCompleted, TimeSpan.FromSeconds(30), "the view to dispose");
		if (dispose.IsFaulted)
		{
			throw dispose.Exception!.GetBaseException();
		}

		if (!window.View.IsDisposed)
		{
			throw new InvalidOperationException("DisposeAsync completed without marking the view disposed.");
		}

		window.Dispose();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(10), "the window to be destroyed");
		WaitFor(() => BackgroundWorkState.Finished >= 1, FromDelay(delay, 30), "background work to finish");
	}

	// disposal is idempotent — the same task for a second call, and a window that survives being disposed
	// twice (the second one from a finalizer path in real apps).
	private static void DisposeTwice(IServiceProvider services, int delay)
	{
		var window = OpenWindow(services, delay);

		WaitFor(() => BackgroundWorkState.Started >= 1, TimeSpan.FromSeconds(60), "background work to start");

		var first = window.View.DisposeAsync().AsTask();
		var second = window.View.DisposeAsync().AsTask();
		if (!ReferenceEquals(first, second))
		{
			throw new InvalidOperationException("The second DisposeAsync started a second teardown.");
		}

		WaitFor(() => first.IsCompleted, TimeSpan.FromSeconds(30), "the view to dispose");
		if (first.IsFaulted)
		{
			throw first.Exception!.GetBaseException();
		}

		window.Dispose();
		window.Dispose();

		if (!window.IsDisposed)
		{
			throw new InvalidOperationException("The window reports itself alive after two Dispose calls.");
		}

		WaitFor(() => BackgroundWorkState.Finished >= 1, FromDelay(delay, 30), "background work to finish");
	}

	// several views on one UI thread share its message queue and synchronization context; closing them at the
	// same time must not interleave teardown into a half-disposed state.
	private static void CloseMultipleWindows(IServiceProvider services, int delay, int count)
	{
		var windows = new List<BlazorWebViewWindow>(count);
		for (var i = 0; i < count; i++)
		{
			var window = OpenWindow(services, delay, throwException: i % 2 == 1);
			windows.Add(window);
		}

		WaitFor(() => BackgroundWorkState.Started >= count, TimeSpan.FromSeconds(90), $"{count} windows to start work");
		Pump.For(s_hold);

		foreach (var window in windows)
		{
			window.Close();
		}

		WaitFor(() => windows.TrueForAll(w => w.IsDisposed), TimeSpan.FromSeconds(60), "all windows to close");
		WaitFor(() => BackgroundWorkState.Finished >= count, FromDelay(delay, 60), $"{count} work items to finish");
	}

	// one environment behind two views. Closing the first must not dispose what the second still uses, and
	// closing the last must not double-dispose the shared environment.
	private static void CloseSharedEnvironment(int delay)
	{
		var environmentsCreated = 0;

		var services = new ServiceCollection();
		services.AddLogging(builder =>
		{
			builder.SetMinimumLevel(LogLevel.Error);
			builder.AddProvider(new CapturingLoggerProvider(s_logged));
		});
		services.AddWin32BlazorWebViewAot().UseSharedCoreWebView2Environment((_, cancellationToken) =>
		{
			Interlocked.Increment(ref environmentsCreated);
			// The runtime call is nullable-returning: a WebView2 that hands back nothing is an environment creation failure.
			return new ValueTask<DirectN.Extensions.Com.IComObject<WebView2.ICoreWebView2Environment>>(
				WebView2.Functions.CreateCoreWebView2EnvironmentWithOptionsAsync(null, null, null)!);
		});

		using var provider = services.BuildServiceProvider();

		var first = OpenWindow(provider, delay);
		var second = OpenWindow(provider, delay);

		WaitFor(() => BackgroundWorkState.Started >= 2, TimeSpan.FromSeconds(90), "both windows to start work");
		Pump.For(s_hold);

		if (Volatile.Read(ref environmentsCreated) != 1)
		{
			throw new InvalidOperationException($"Expected one shared environment, created {environmentsCreated}.");
		}

		first.Close();
		WaitFor(() => first.IsDisposed, TimeSpan.FromSeconds(30), "the first window to close");

		// The survivor keeps working: its browser was not owned by the window that went away.
		Pump.For(s_hold);
		if (BackgroundWorkState.Started < 2)
		{
			throw new InvalidOperationException("Closing one view stopped the other's work.");
		}

		second.Close();
		WaitFor(() => second.IsDisposed, TimeSpan.FromSeconds(30), "the second window to close");
		WaitFor(() => BackgroundWorkState.Finished >= 2, FromDelay(delay, 60), "both work items to finish");
	}

	// kill the browser process and check that the crash is reported through BrowserProcessFailed, that the
	// window still closes cleanly, and that no exception from an already-dead COM object escapes.
	private static void BrowserProcessCrash(IServiceProvider services, int delay)
	{
		var before = SnapshotBrowserProcesses();

		var window = OpenWindow(services, delay);
		WaitFor(() => BackgroundWorkState.Started >= 1, TimeSpan.FromSeconds(60), "background work to start");

		var ours = WaitForOwnBrowserProcess(before);
		if (ours.Count == 0)
		{
			throw new InvalidOperationException(
				"No msedgewebview2 process appeared after starting the view; cannot simulate a crash on this machine.");
		}

		WaitFor(() => window.View.WebView is not null, TimeSpan.FromSeconds(30), "the browser to be reachable");

		var crashed = new ManualResetEventSlim();
		string? reportedKind = null;
		bool? terminal = null;
		window.View.BrowserProcessFailed += (_, e) =>
		{
			reportedKind = e.ProcessFailedKind.ToString();
			terminal = e.IsTerminal;
			crashed.Set();
		};

		foreach (var process in ours)
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception exception)
			{
				// A process that already exited on its own is the outcome we were trying to cause.
				s_notes.Add($"kill({process.Id}): {exception.GetType().Name}");
			}
		}

		Pump.For(TimeSpan.FromSeconds(2));
		if (!crashed.Wait(TimeSpan.FromSeconds(30)))
		{
			throw new InvalidOperationException("The browser died but BrowserProcessFailed never fired.");
		}

		s_notes.Add($"reported {reportedKind}, terminal={terminal}");

		// Closing after a crash is the interesting part: every COM call in teardown now returns a dead-object HRESULT.
		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close after a browser crash");
	}

	// work dispatched from another thread while the view is being torn down must complete or fault — never
	// hang — and its failure must not reach the host's unhandled path as an app bug.
	private static void DispatcherWorkDuringShutdown(IServiceProvider services, int delay)
	{
		var window = OpenWindow(services, delay);
		WaitFor(() => BackgroundWorkState.Started >= 1, TimeSpan.FromSeconds(60), "background work to start");

		var dispatcher = window.View.Dispatcher;
		var tasks = new List<Task>();
		using var dispatched = new ManualResetEventSlim();
		using var worker = new ManualResetEventSlim();

		var thread = new Thread(() =>
		{
			for (var i = 0; i < 50; i++)
			{
				tasks.Add(dispatcher.InvokeAsync(() => { }));
			}

			dispatched.Set();
			worker.Wait(TimeSpan.FromSeconds(30));
		});
		thread.IsBackground = true;
		thread.Start();

		if (!dispatched.Wait(TimeSpan.FromSeconds(30)))
		{
			throw new TimeoutException("Timed out dispatching work to the UI thread.");
		}

		// Teardown begins while that work is queued behind it.
		var dispose = window.View.DisposeAsync().AsTask();
		window.Close();

		worker.Set();
		thread.Join(TimeSpan.FromSeconds(30));

		WaitFor(() => tasks.TrueForAll(t => t.IsCompleted), TimeSpan.FromSeconds(30), "all dispatched work to settle");
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");

		var ran = 0;
		foreach (var task in tasks)
		{
			if (task.IsFaulted)
			{
				// Expected for the items that arrived after the renderer went away. Any exception type is acceptable here as
				// long as it stayed attached to its own task, which is what this scenario is about.
				continue;
			}

			ran++;
		}

		s_notes.Add($"{ran}/{tasks.Count} dispatched items ran before shutdown");
	}

	// The queue's contract under teardown: once it stops accepting work, callers get a faulted task rather than
	// one that never completes. A hung InvokeAsync is the failure mode this guards.
	// G14 regression: a composition-hosted browser receives no OS input — every click must be re-delivered by the host.
	// This sends genuine Win32 mouse input at the window's client center; the page's full-viewport @onclick layer counts
	// it. A wrong coordinate space anywhere in the chain (client vs screen) leaves Clicks at 0 and this times out.
	private static void ClickReachesPage(IServiceProvider services, int delay)
	{
		var window = OpenWindow(services, delay);
		WaitFor(() => BackgroundWorkState.Started >= 1, TimeSpan.FromSeconds(60), "the page to render");

		// Let the browser finish wiring Blazor's event delegation after the first render batch.
		Pump.For(TimeSpan.FromMilliseconds(750));

		var hwnd = window.Handle;

		// mouse_event delivers to whatever window is TOPMOST at the cursor point, so the test must own that pixel.
		// Raise our window in every retry iteration: a background process cannot take the foreground outright (observed:
		// Windows Terminal swallowing the click), so the raise borrows the foreground thread's input queue — the classic
		// workaround. The client rect is re-read each pass because a DPI change can resize the window mid-test.
		var occlusionNoted = false;
		var ownsThePixel = false;
		var center = default(POINT);
		var deadline = Stopwatch.StartNew();
		while (deadline.Elapsed < TimeSpan.FromSeconds(10) && !ownsThePixel)
		{
			RaiseAboveForeground(hwnd);

			if (!GetClientRect(hwnd, out var client))
			{
				throw new InvalidOperationException("GetClientRect failed on a live window.");
			}

			// The click point: physical client center, converted to the screen space SetCursorPos speaks.
			center = new POINT((client.Right - client.Left) / 2, (client.Bottom - client.Top) / 2);
			if (!ClientToScreen(hwnd, ref center) || !SetCursorPos(center.x, center.y))
			{
				throw new InvalidOperationException("Could not move the cursor over the test window.");
			}

			Pump.For(TimeSpan.FromMilliseconds(50));
			var covering = WindowFromPoint(center);
			ownsThePixel = covering == hwnd;
			if (!ownsThePixel && !occlusionNoted)
			{
				occlusionNoted = true;
				var name = new char[256];
				_ = GetClassName(covering, name, name.Length);
				_ = GetWindowThreadProcessId(covering, out var occludingPid);
				s_notes.Add($"cursor at ({center.x},{center.y}) is over '{new string(name, 0, Array.IndexOf(name, '\0'))}' hwnd={covering.Value:x} pid={occludingPid}, not {hwnd.Value:x} — will fall back to posted input");
			}
		}

		if (ownsThePixel)
		{
			// Let Windows deliver hover first (the page must consider the pointer inside), then press and release.
			Pump.For(TimeSpan.FromMilliseconds(250));
			mouse_event(MoEventLeftDown, 0, 0, 0, UIntPtr.Zero);
			Pump.For(TimeSpan.FromMilliseconds(120));
			mouse_event(MoEventLeftUp, 0, 0, 0, UIntPtr.Zero);
		}
		else
		{
			// The desktop is occupied (e.g. the terminal running this harness covers our pixel) and a background process
			// cannot win the foreground lock. Post the same messages straight to the WndProc instead: everything from the
			// window procedure onward — hit-test-free client coords, forwarding, SendMouseInput, Blazor dispatch — is still
			// proven; only Windows' own pixel-to-window routing is bypassed, which is OS behaviour, not ours.
			s_notes.Add("input posted directly to the WndProc (pixel owned by another window)");
			var clientPoint = new POINT((GetClientWidth(hwnd)) / 2, (GetClientHeight(hwnd)) / 2);
			_ = PostMessageW(hwnd, 0x0200 /* WM_MOUSEMOVE */, IntPtr.Zero, (nint)(((uint)clientPoint.y << 16) | (ushort)clientPoint.x));
			Pump.For(TimeSpan.FromMilliseconds(120));
			_ = PostMessageW(hwnd, 0x0201 /* WM_LBUTTONDOWN */, (nint)1 /* MK_LBUTTON */, (nint)(((uint)clientPoint.y << 16) | (ushort)clientPoint.x));
			Pump.For(TimeSpan.FromMilliseconds(120));
			_ = PostMessageW(hwnd, 0x0202 /* WM_LBUTTONUP */, IntPtr.Zero, (nint)(((uint)clientPoint.y << 16) | (ushort)clientPoint.x));
		}

		WaitFor(() => BackgroundWorkState.Clicks >= 1, TimeSpan.FromSeconds(15), "the synthetic click to reach the page");

		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");
	}

	// G16 regression: the resize band must be grabbable by hand. This drags the right edge inward with genuine Win32
	// mouse input and asserts the window actually shrinks — a too-thin hit target (the original bug) leaves the width
	// untouched because the drag lands on the WebView page instead of the band.
	private static void EdgeDragResizesWindow(IServiceProvider services, int delay)
	{
		var window = OpenWindow(services, delay);
		WaitFor(() => BackgroundWorkState.Started >= 1, TimeSpan.FromSeconds(60), "the page to render");

		var hwnd = window.Handle;
		RaiseAboveForeground(hwnd);
		Pump.For(TimeSpan.FromMilliseconds(250));

		if (!GetWindowRect(hwnd, out var before))

		{
			throw new InvalidOperationException("GetWindowRect failed on a live window.");
		}

		// Four pixels inside the right edge: inside the 8-DIP default band at every DPI we support, yet far enough off
		// the page content that a missing band would leave the width untouched.
		var edgeX = before.Right - 4;
		var midY = (before.Top + before.Bottom) / 2;
		if (!SetCursorPos(edgeX, midY))
		{
			throw new InvalidOperationException("Could not move the cursor to the window edge.");
		}

		Pump.For(TimeSpan.FromMilliseconds(150));

		// G17 regression: hovering the band must show the horizontal size cursor. The stock-cursor handles are shared
		// system-wide, so comparing against LoadCursorW(NULL, IDC_SIZEWE) identifies it exactly.

		var cursorInfo = default(CursorInfo);
		cursorInfo.cbSize = (uint)Marshal.SizeOf<CursorInfo>();
		if (GetCursorInfo(ref cursorInfo) && cursorInfo.hCursor != LoadCursorW(IntPtr.Zero, IdcSizeWe))
		{
			throw new InvalidOperationException(
				$"Hovering the resize band showed cursor 0x{cursorInfo.hCursor.ToInt64():x}, not IDC_SIZEWE (0x{LoadCursorW(IntPtr.Zero, IdcSizeWe).ToInt64():x}).");
		}

		if (WindowFromPoint(new POINT(edgeX, midY)) != hwnd)
		{
			// A modal size loop cannot be faked with posted messages, so on an occupied desktop this scenario can only
			// say so rather than fight the foreground lock a second time.
			s_notes.Add("edge pixel owned by another window — resize drag skipped");
			window.Close();
			WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");
			return;
		}

		// The size loop is MODAL: it runs inside DefWindowProc and consumes input until the button goes up, so this
		// thread cannot inject mid-drag moves — it would be stuck in DispatchMessage. The gesture comes from a helper
		// thread while our pump dispatches WM_NCLBUTTONDOWN into the loop.
		var drag = new Thread(() =>
		{
			mouse_event(MoEventLeftDown, 0, 0, 0, UIntPtr.Zero);
			Thread.Sleep(120);
			for (var i = 1; i <= 15; i++)
			{
				SetCursorPos(edgeX - (i * 10), midY);
				Thread.Sleep(40);
			}

			Thread.Sleep(120);
			mouse_event(MoEventLeftUp, 0, 0, 0, UIntPtr.Zero);
		});

		drag.IsBackground = true;
		drag.Start();
		Pump.For(TimeSpan.FromSeconds(3));
		drag.Join(TimeSpan.FromSeconds(5));

		WaitFor(
			() => GetWindowRect(hwnd, out var now) && (before.Right - before.Left) - (now.Right - now.Left) > 60,
			TimeSpan.FromSeconds(10),
			"the window to shrink after the edge drag");

		s_notes.Add($"edge drag resized width {before.Right - before.Left} -> {(GetWindowRect(hwnd, out var fin) ? fin.Right - fin.Left : -1)}");

		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");
	}

	// User-reported 2026-11-24 on the native title-bar sample (samples/Win32BlazorWebViewAot.Html): a window with a
// native caption could not be
	// resized. Native chrome answers WM_NCHITTEST through DefWindowProcW, which wants the screen point packed the Win32 way
	// (x in the low word, y in the high word). Getting that wrong makes every frame pixel HTNOWHERE: no size cursor and no
	// resize drag — while the borderless path, which computes hit codes itself, keeps working. So this checks BOTH halves of
	// the same answer on a default (native-caption) window.
	private static void NativeFrameDragResizesWindow(IServiceProvider services, int delay)
	{
		// Deliberately NOT OpenWindow: that one is borderless. This is the library's default frame.
		var window = new BlazorWebViewWindow("Win32BlazorWebViewAot.LifetimeTests") { QuitOnDestroy = false };
		Configure(window.View, services, delay);
		window.Show();

		var start = window.View.StartAsync();
		WaitFor(() => start.IsCompleted, TimeSpan.FromSeconds(90), "WebView2 to initialize");
		if (start.IsFaulted)
		{
			throw start.Exception!.GetBaseException();
		}

		var hwnd = window.Handle;
		RaiseAboveForeground(hwnd);
		Pump.For(TimeSpan.FromMilliseconds(250));

		if (!GetWindowRect(hwnd, out var before))
		{
			throw new InvalidOperationException("GetWindowRect failed on a live window.");
		}

		// Four pixels inside the bottom frame: Windows puts its resize border there at every DPI we support.
		var midX = (before.Left + before.Right) / 2;
		var edgeY = before.Bottom - 4;
		if (!SetCursorPos(midX, edgeY))
		{
			throw new InvalidOperationException("Could not move the cursor to the window frame.");
		}

		Pump.For(TimeSpan.FromMilliseconds(200));

		// Half one of the answer: the frame must report itself as a vertical resize target, which shows up as IDC_SIZE_NS.
		var cursorInfo = default(CursorInfo);
		cursorInfo.cbSize = (uint)Marshal.SizeOf<CursorInfo>();
		if (GetCursorInfo(ref cursorInfo) && cursorInfo.hCursor != LoadCursorW(IntPtr.Zero, IdcSizeNs))
		{
			throw new InvalidOperationException(
				$"Hovering the native frame showed cursor 0x{cursorInfo.hCursor.ToInt64():x}, not IDC_SIZE_NS (0x{LoadCursorW(IntPtr.Zero, IdcSizeNs).ToInt64():x}) — WM_NCHITTEST is not reaching DefWindowProcW with a usable point.");
		}

		if (WindowFromPoint(new POINT(midX, edgeY)) != hwnd)
		{
			s_notes.Add("frame pixel owned by another window — resize drag skipped");
			window.Close();
			WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");
			return;
		}

		// Half two: the modal size loop runs inside DefWindowProc, so the gesture comes from a helper thread while this
		// thread's pump dispatches WM_NCLBUTTONDOWN into it (same trick as scenario 12).
		var drag = new Thread(() =>
		{
			mouse_event(MoEventLeftDown, 0, 0, 0, UIntPtr.Zero);
			Thread.Sleep(120);
			for (var i = 1; i <= 15; i++)
			{
				SetCursorPos(midX, edgeY + (i * 10));
				Thread.Sleep(40);
			}

			Thread.Sleep(120);
			mouse_event(MoEventLeftUp, 0, 0, 0, UIntPtr.Zero);
		});

		drag.IsBackground = true;
		drag.Start();
		Pump.For(TimeSpan.FromSeconds(3));
		drag.Join(TimeSpan.FromSeconds(5));

		WaitFor(
			() => GetWindowRect(hwnd, out var now) && (now.Bottom - now.Top) - (before.Bottom - before.Top) > 60,
			TimeSpan.FromSeconds(10),
			"the native-caption window to grow after the frame drag");

		s_notes.Add($"native frame drag resized height {before.Bottom - before.Top} -> {(GetWindowRect(hwnd, out var fin) ? fin.Bottom - fin.Top : -1)}");

		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");
	}

	// A bug in one handler must not be a hard process crash. WM_MOVE lands on this thread's message loop and
	// OnMoved throws on purpose; the throw starts inside user32, so before the library had an exception boundary it unwound
	// native frames and the runtime failed the process — nothing here could catch it, and the scenario "fails" by never
	// returning. After the boundary the dispatch answers Windows, reports the failure, and this window is still usable.
	private static void ThrowingMessageHandlerIsReported(IServiceProvider services, int delay)
	{
		// The library's failure channel for this boundary: an app sees the exception instead of the process dying with it.
		var reported = new List<Win32UnhandledExceptionEventArgs>();
		EventHandler<Win32UnhandledExceptionEventArgs> watch = (_, args) =>
		{
			lock (reported)
			{
				reported.Add(args);
			}
		};

		Win32Window.UnhandledMessageException += watch;
		try
		{
			MoveUntilHandlerThrows();
		}
		finally
		{
			Win32Window.UnhandledMessageException -= watch;
		}

		if (reported.Count == 0)
		{
			throw new InvalidOperationException(
				"The throwing handler was swallowed: Win32Window.UnhandledMessageException never fired.");
		}

		var first = reported[0].Exception;
		if (first is not InvalidOperationException || !first.Message.Contains("Deliberate handler failure", StringComparison.Ordinal))
		{
			throw new InvalidOperationException($"The boundary reported the wrong exception: {first.GetType().Name}: {first.Message}");
		}

		s_notes.Add($"boundary reported {reported.Count} failure(s) as {first.GetType().Name}");
	}

	/// <summary>A plain top-level window whose WM_MOVE handler always fails, with a marker naming the failure.</summary>
	private sealed class ThrowingWindow(string title) : Win32Window(title)
	{
		/// <summary>Text carried by the exception the handler throws.</summary>
		public string Marker { get; set; } = "Deliberate handler failure";

		protected override void OnMoved() => throw new InvalidOperationException($"{Marker}.");
	}

	/// <summary>Shows a window whose WM_MOVE handler throws, moves it once, and closes it again.</summary>
	private static void MoveUntilHandlerThrows()
	{
		var window = new ThrowingWindow("Win32BlazorWebViewAot.LifetimeTests") { QuitOnDestroy = false };
		window.Show();
		Pump.For(TimeSpan.FromMilliseconds(250));

		if (!GetWindowRect(window.Handle, out var before))
		{
			throw new InvalidOperationException("GetWindowRect failed on a live window.");
		}

		// A move is what raises WM_MOVE. It is dispatched inline on this thread, so the handler throw happens inside the
		// call below rather than in a pump turn we would have to wait for.
		_ = SetWindowPos(
			window.Handle,
			HWND.Null,
			before.Left + 40,
			before.Top + 40,
			0,
			0,
			SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
		Pump.For(TimeSpan.FromMilliseconds(250));

		if (window.IsDisposed)
		{
			throw new InvalidOperationException("A throwing handler destroyed the window instead of being reported.");
		}

		// The window must still answer afterwards: the boundary has to leave the message loop working, not merely keep the CLR
		// alive. Closing proves both — WM_CLOSE reaches DefWindowProcW and the instance is torn down normally.
		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close after its handler threw");
	}

	// The other half of the boundary's contract: an app that marks a reported failure handled has said it dealt with it, so
	// nothing is promoted to the runtime's unhandled path; one that does not must still see it there. Absence alone proves
	// nothing, so phase A runs first as a control and fails if promotion is not observable in this process at all.
	private static void HandledFlagControlsPromotion(IServiceProvider services, int delay)
	{
		// Phase A (control): no subscriber sets Handled, so the failure must reach TaskScheduler.UnobservedTaskException.
		DrainUnobserved();
		ThrowOnceAndCollect("Deliberate unhandled failure", handled: false);
		if (!UnobservedContains("Deliberate unhandled failure"))
		{
			throw new InvalidOperationException(
				"An unhandled boundary failure never reached the unobserved-task path, so phase B would prove nothing.");
		}

		// Phase B: the same failure with a subscriber that marks it handled must stay out of that path.
		DrainUnobserved();
		ThrowOnceAndCollect("Deliberate suppressed failure", handled: true);
		if (UnobservedContains("Deliberate suppressed failure"))
		{
			throw new InvalidOperationException(
				"Handled was ignored: the failure was promoted to an unhandled exception after a subscriber claimed it.");
		}
	}

	/// <summary>Moves a window whose handler throws once, then gives finalizers a turn so any promotion becomes observable.</summary>
	private static void ThrowOnceAndCollect(string marker, bool handled)
	{
		EventHandler<Win32UnhandledExceptionEventArgs> watch = (_, args) =>
		{
			if (handled)
			{
				args.Handled = true;
			}
		};

		Win32Window.UnhandledMessageException += watch;
		try
		{
			var window = new ThrowingWindow("Win32BlazorWebViewAot.LifetimeTests") { QuitOnDestroy = false, Marker = marker };
			window.Show();
			Pump.For(TimeSpan.FromMilliseconds(150));

			if (!GetWindowRect(window.Handle, out var before))
			{
				throw new InvalidOperationException("GetWindowRect failed on a live window.");
			}

			_ = SetWindowPos(
				window.Handle,
				HWND.Null,
				before.Left + 30,
				before.Top + 30,
				0,
				0,
				SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);

			Pump.For(TimeSpan.FromMilliseconds(150));
			window.Close();
			WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");
		}
		finally
		{
			Win32Window.UnhandledMessageException -= watch;
		}

		// Promotion is a faulted task nobody awaits, so it surfaces when the task is finalized.
		for (var i = 0; i < 2; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
		}

		Pump.For(TimeSpan.FromMilliseconds(250));
	}

	private static void DrainUnobserved()
	{
		while (s_unobserved.TryDequeue(out _))
		{
		}
	}

	private static bool UnobservedContains(string marker) =>
		s_unobserved.ToArray().Any(message => message.Contains(marker, StringComparison.Ordinal));

	// The second entry point of the same boundary: the message-only window that drains Win32MessageQueue is a window
	// procedure too, and its own reporting path can throw — a subscriber to Win32MessageQueue.UnhandledException that fails
	// is called from inside it. Same consequence as scenario 15 (the throw starts under user32), so the same boundary applies.
	private static void ThrowingQueueSubscriberIsReported(IServiceProvider services, int delay)
	{
		var reported = new List<Win32UnhandledExceptionEventArgs>();
		EventHandler<Win32UnhandledExceptionEventArgs> watch = (_, args) =>
		{
			lock (reported)
			{
				reported.Add(args);
			}
		};

		var queue = Win32MessageQueue.GetOrCreateForCurrentThread();
		EventHandler<Win32UnhandledExceptionEventArgs> badSubscriber = (_, args) =>
		{
			if (args.Exception.Message.Contains("Deliberate queued failure", StringComparison.Ordinal))
			{
				throw new InvalidOperationException("Deliberate subscriber failure.");
			}
		};

		Win32Window.UnhandledMessageException += watch;
		queue.UnhandledException += badSubscriber;
		try
		{
			if (!queue.TryPost(static () => throw new InvalidOperationException("Deliberate queued failure.")))
			{
				throw new InvalidOperationException("The queue refused the work, so the scenario proved nothing.");
			}

			Pump.For(TimeSpan.FromMilliseconds(500));

			// The drain loop must still be running: work posted after the failure has to reach the UI thread, or every view on
			// this queue would silently stop making progress.
			var drained = false;
			if (!queue.TryPost(() => drained = true))
			{
				throw new InvalidOperationException("The queue stopped accepting work after a subscriber threw.");
			}

			WaitFor(() => drained, TimeSpan.FromSeconds(10), "the queue to drain later work after a subscriber threw");

			if (reported.Count == 0)
			{
				throw new InvalidOperationException(
					"The throwing subscriber was swallowed: Win32Window.UnhandledMessageException never fired.");
			}

			s_notes.Add($"boundary reported {reported.Count} failure(s) from the queue drain");
		}
		finally
		{
			queue.UnhandledException -= badSubscriber;
			Win32Window.UnhandledMessageException -= watch;
		}
	}

	// Regression: a borderless window has no OS caption, so moving it must come from the page — mousedown on a drag region
	// calls Win32Window.BeginDrag, which starts the same native move loop Windows uses for a caption drag. This presses on
	// the harness page's caption strip and drags with genuine Win32 input; the window origin must follow.
	private static void CaptionDragMovesWindow(IServiceProvider services, int delay)
	{
		var window = OpenWindow(services, delay);
		WaitFor(() => BackgroundWorkState.Started >= 1, TimeSpan.FromSeconds(60), "the page to render");

		// The strip's mousedown handler invokes whatever is here; wire it to this window.
		WindowDragHook.BeginDrag = window.BeginDrag;

		var hwnd = window.Handle;
		RaiseAboveForeground(hwnd);
		Pump.For(TimeSpan.FromMilliseconds(250));

		if (!GetWindowRect(hwnd, out var before))
		{
			throw new InvalidOperationException("GetWindowRect failed on a live window.");
		}

		// A point inside the 40-px caption strip, clear of the resize band at the very edge.
		var startX = before.Left + Math.Max(80, (before.Right - before.Left) / 2);
		var startY = before.Top + 15;
		if (!SetCursorPos(startX, startY))
		{
			throw new InvalidOperationException("Could not move the cursor over the caption strip.");
		}

		Pump.For(TimeSpan.FromMilliseconds(150));

		if (WindowFromPoint(new POINT(startX, startY)) != hwnd)
		{
			// Like scenario 12: a modal move loop cannot be faked with posted messages, so on an occupied desktop
			// this can only say so rather than fight the foreground lock.
			s_notes.Add("caption pixel owned by another window — move drag skipped");
			WindowDragHook.BeginDrag = null;
			window.Close();
			WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");
			return;
		}

		// The move loop is modal inside DefWindowProc, so the gesture comes from a helper thread while this one
		// dispatches into the loop. The initial pause lets Blazor's mousedown handler reach BeginDrag — the button
		// must still be down when the native loop starts, which is what makes the OS keep tracking the pointer.
		var drag = new Thread(() =>
		{
			mouse_event(MoEventLeftDown, 0, 0, 0, UIntPtr.Zero);
			Thread.Sleep(600);
			for (var i = 1; i <= 15; i++)
			{
				SetCursorPos(startX + (i * 8), startY + (i * 5));
				Thread.Sleep(40);
			}

			Thread.Sleep(120);
			mouse_event(MoEventLeftUp, 0, 0, 0, UIntPtr.Zero);
		});

		drag.IsBackground = true;
		drag.Start();
		Pump.For(TimeSpan.FromSeconds(3));
		drag.Join(TimeSpan.FromSeconds(5));

		WaitFor(
			() => GetWindowRect(hwnd, out var now) && (now.Left - before.Left) > 60 && (now.Top - before.Top) > 40,
			TimeSpan.FromSeconds(10),
			"the window to follow the caption drag");

		s_notes.Add(
			$"caption drag moved origin {before.Left},{before.Top} -> " +
			$"{(GetWindowRect(hwnd, out var fin) ? $"{fin.Left},{fin.Top}" : "?")}");

		WindowDragHook.BeginDrag = null;
		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	private const uint MoEventLeftDown = 0x0002;
	private const uint MoEventLeftUp = 0x0004;

	[DllImport("user32.dll")]
	private static extern bool GetClientRect(HWND hWnd, out NativeRect lpRect);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(HWND hWnd, out NativeRect lpRect);

	[StructLayout(LayoutKind.Sequential)]
	private struct CursorInfo
	{
		public uint cbSize;
		public uint flags;
		public IntPtr hCursor;
		public int ptX;
		public int ptY;
	}

	[DllImport("user32.dll")]
	private static extern bool GetCursorInfo(ref CursorInfo pci);

	[DllImport("user32.dll")]
	private static extern IntPtr LoadCursorW(IntPtr hInstance, uint lpCursorName);

	private const uint IdcSizeWe = 32644;

	// MAKEINTRESOURCE(IDC_SIZE_NS): the vertical size cursor a native frame shows over its top/bottom border.
	private const uint IdcSizeNs = 32645;

	[DllImport("user32.dll")]
	private static extern bool ClientToScreen(HWND hWnd, ref POINT lpPoint);

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	[DllImport("user32.dll")]
	private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("user32.dll")]
	private static extern HWND WindowFromPoint(POINT point);

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(HWND hWnd);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(HWND hWnd, HWND hWndInsertAfter, int x, int y, int cx, int cy, SET_WINDOW_POS_FLAGS uFlags);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

	[DllImport("user32.dll")]
	private static extern IntPtr PostMessageW(IntPtr hWnd, uint message, IntPtr wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern HWND GetForegroundWindow();

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

	[DllImport("user32.dll")]
	private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

	[DllImport("user32.dll")]
	private static extern bool BringWindowToTop(IntPtr hWnd);

	/// <summary>Raises <paramref name="hwnd"/> above the current foreground window from a background process.</summary>
	private static void RaiseAboveForeground(HWND hwnd)
	{
		var foreground = GetForegroundWindow();
		var foregroundThread = GetWindowThreadProcessId(foreground, out _);
		var ownThread = GetCurrentThreadId();

		// Sharing input queues makes Windows treat us as if the user asked for this raise, defeating the foreground lock.
		var attached = foregroundThread != 0 && AttachThreadInput(ownThread, foregroundThread, true);
		_ = BringWindowToTop(hwnd);
		_ = SetForegroundWindow(hwnd);
		// hWndInsertAfter NULL is HWND_TOP; no move, no resize. Activation stays with the borrowed-foreground handshake.
		_ = SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
			SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
		if (attached)
		{
			_ = AttachThreadInput(ownThread, foregroundThread, false);
		}
	}

	private static int GetClientWidth(HWND hwnd)
	{
		_ = GetClientRect(hwnd, out var r);
		return r.Right - r.Left;
	}

	private static int GetClientHeight(HWND hwnd)
	{
		_ = GetClientRect(hwnd, out var r);
		return r.Bottom - r.Top;
	}

	private const int GWL_EXSTYLE = -20;
	private const long WS_EX_TOPMOST = 0x8;

	private static void WorkOnClosedQueue(IServiceProvider services, int delay)
	{
		var window = OpenWindow(services, delay);
		WaitFor(() => BackgroundWorkState.Started >= 1, TimeSpan.FromSeconds(60), "background work to start");

		var dispatcher = window.View.Dispatcher;
		var queue = Win32MessageQueue.GetOrCreateForCurrentThread();

		var before = dispatcher.InvokeAsync(() => 1);
		if (before.IsFaulted)
		{
			throw before.Exception!.GetBaseException();
		}

		queue.Complete();

		var dropped = dispatcher.InvokeAsync(() => throw new InvalidOperationException("must not run"));
		WaitFor(() => dropped.IsCompleted, TimeSpan.FromSeconds(15), "work on a closed queue to settle");

		if (!dropped.IsFaulted)
		{
			throw new InvalidOperationException("Work posted to a closed queue completed instead of faulting.");
		}

		if (dropped.Exception!.GetBaseException() is not Win32DispatcherException)
		{
			throw new InvalidOperationException(
				$"Expected Win32DispatcherException for dropped work, got {dropped.Exception.GetBaseException().GetType().Name}.");
		}

		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");
	}

	// A scheme with no handler registered anywhere: if the host shell-executes it, nothing visible happens and only a
	// "Failed to launch" warning shows; if the host refuses by policy, a refusal log names the scheme before any attempt.
	private const string ProbeSchemeUri = "win32aotblazor-s1probe://payload";

	/// <summary>
	/// The page calls window.open() with a dead custom scheme. That request must surface on the app's UrlLoading event —
	/// today it bypasses every policy and goes straight to Process.Start(UseShellExecute) — and the launch itself must be
	/// refused because the scheme is not http/https/mailto.
	/// </summary>
	private static void NewWindowSchemeAllowList(IServiceProvider services, int delay)
	{
		var window = OpenWindow(services, delay, newWindowUri: ProbeSchemeUri);
		WaitFor(() => BackgroundWorkState.Started >= 1, TimeSpan.FromSeconds(60), "background work to start");

		// The new-window request must reach the app's UrlLoading event with the probe URI.
		WaitFor(
			() => s_urlLoading.Any(entry => entry.Contains(ProbeSchemeUri, StringComparison.OrdinalIgnoreCase)),
			TimeSpan.FromSeconds(30),
			"the UrlLoading event to report the new-window request");

		// And it must have been refused before any shell attempt: a policy refusal, not a launch failure.
		WaitFor(
			() => s_logged.Any(entry => entry.Contains("Refused", StringComparison.Ordinal) && entry.Contains(ProbeSchemeUri, StringComparison.OrdinalIgnoreCase)),
			TimeSpan.FromSeconds(10),
			"a refusal log naming the probe scheme");

		if (s_logged.Any(entry => entry.Contains("Failed to launch", StringComparison.Ordinal)))
		{
			throw new InvalidOperationException($"The probe scheme reached Process.Start: {string.Join(" | ", s_logged)}");
		}

		window.Close();
		WaitFor(() => window.IsDisposed, TimeSpan.FromSeconds(30), "the window to close");
	}

	/// <summary>
	/// The queue's window class used to be named after the managed thread ID, which the CLR recycles — and its registration
	/// result was ignored. A second queue whose name was already taken therefore bound its message window to the FIRST
	/// registrant's window procedure: posted work never ran. Managed IDs do not recycle on demand, so this scenario forces
	/// the collision directly: it pre-registers a decoy class under the exact per-thread name before creating the queue.
	/// With one process-wide class and HWND-routed dispatch there is no thread-derived name to collide with, and the work runs.
	/// </summary>
	private static void QueueOnRecycledThreadIdRunsWork(IServiceProvider services, int delay)
	{
		using var ready = new ManualResetEventSlim(false);
		using var finished = new ManualResetEventSlim(false);
		Win32MessageQueue? queue = null;
		var ran = false;
		Exception? threadError = null;

		var worker = new Thread(() =>
		{
			try
			{
				// The name the per-thread registration would choose for THIS thread. Registering it first with a decoy
				// procedure reproduces "class already registered" exactly as a recycled managed ID would.
				var className = $"Win32BlazorWebViewAot.Queue.{Environment.CurrentManagedThreadId}";
				RootDecoyProcedure(className);

				queue = new Win32MessageQueue();
				ready.Set();

				while (!finished.Wait(TimeSpan.Zero))
				{
					PumpOnce();
				}
			}
			catch (Exception exception)
			{
				threadError = exception;
				ready.Set();
			}
			finally
			{
				queue?.Dispose();
				finished.Set();
			}
		});
		worker.SetApartmentState(ApartmentState.STA);
		worker.Start();

		if (!ready.Wait(TimeSpan.FromSeconds(15)))
		{
			finished.Set();
			worker.Join(TimeSpan.FromSeconds(10));
			throw new InvalidOperationException("The decoy-class thread never reported ready.");
		}

		if (threadError is not null)
		{
			throw new InvalidOperationException($"Creating a queue against a taken class name threw: {threadError.Message}", threadError);
		}

		// Post work to the queue that was built on top of the colliding class and wait for it to run.
		var posted = queue!.InvokeAsync(() => Volatile.Write(ref ran, true));
		var deadline = Stopwatch.StartNew();
		while (!posted.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(15))
		{
			PumpOnce(); // the main thread owns a queue too; keep it alive while waiting
		}

		finished.Set();
		worker.Join(TimeSpan.FromSeconds(10));

		if (posted.IsFaulted)
		{
			throw new InvalidOperationException($"The posted work faulted: {posted.Exception!.GetBaseException().Message}", posted.Exception);
		}

		if (!Volatile.Read(ref ran))
		{
			throw new InvalidOperationException(
				"Work posted to a queue whose window class name was already taken never ran — the window bound to the decoy procedure.");
		}
	}

	// Decoy window procedures, rooted for the life of the process: Windows calls through these pointers while the class lives.
	private static readonly List<DirectN.WNDPROC> s_decoyProcedures = [];

	private static void RootDecoyProcedure(string className)
	{
		var decoy = new DirectN.WNDPROC(static (hwnd, message, wParam, lParam) =>
			DirectN.Functions.DefWindowProcW(hwnd, message, wParam, lParam));
		s_decoyProcedures.Add(decoy);

		var name = Marshal.StringToHGlobalUni(className);
		try
		{
			var windowClass = new DirectN.WNDCLASSW
			{
				style = 0,
				lpfnWndProc = Marshal.GetFunctionPointerForDelegate(decoy),
				hInstance = (DirectN.HINSTANCE)(nint)DirectN.Functions.GetModuleHandleW(default),
				lpszClassName = new DirectN.PWSTR(name),
			};

			if (DirectN.Functions.RegisterClassW(in windowClass) == 0)
			{
				throw new InvalidOperationException($"Decoy RegisterClassW failed for '{className}': Win32 error {Marshal.GetLastWin32Error()}.");
			}
		}
		finally
		{
			Marshal.FreeHGlobal(name);
		}
	}

	private static IServiceProvider CreateServices()
	{
		var services = new ServiceCollection();
		services.AddWin32BlazorWebViewAot();
		services.AddLogging(builder =>
		{
			// Warning, not Error: scenario 18 asserts on the refusal log line the navigation handler writes at Warning.
			builder.SetMinimumLevel(LogLevel.Warning);
			builder.AddProvider(new CapturingLoggerProvider(s_logged));
		});

		return services.BuildServiceProvider();
	}

	/// <summary>
		/// Opens a window for the bulk of the scenarios. Borderless on purpose: that is the frame whose hit-test, DWM and
		/// maximize handling is the delicate part, so it stays covered here. The two hand-built windows in
		/// <see cref="CloseDuringInitialization"/> and <see cref="CloseDuringNavigation"/> use the default
		/// <see cref="BlazorWebViewWindow"/>, which now carries a native title bar, so both frames are exercised.
		/// </summary>
	private static BlazorWebViewWindow OpenWindow(
		IServiceProvider services,
		int delay,
		bool throwException = false,
		bool awaitJsInterop = false,
		string? newWindowUri = null)
	{
		var window = new BorderlessBlazorWebViewWindow("Win32BlazorWebViewAot.LifetimeTests")
		{
			// The harness owns the loop; a scenario closing its last window must not post WM_QUIT and end the run.
			QuitOnDestroy = false,
		};

		Configure(window.View, services, delay, throwException, awaitJsInterop, newWindowUri);

		window.Show();

		var start = window.View.StartAsync();
		WaitFor(() => start.IsCompleted, TimeSpan.FromSeconds(90), "WebView2 to initialize");
		if (start.IsFaulted)
		{
			throw start.Exception!.GetBaseException();
		}

		return window;
	}

	private static void Configure(
		BlazorWebView view,
		IServiceProvider services,
		int delay,
		bool throwException = false,
		bool awaitJsInterop = false,
		string? newWindowUri = null)
	{
		view.HostPage = @"wwwroot\index.html";
		view.Services = services;
		view.RootComponents.Add<App>("#app", new Dictionary<string, object?>
		{
			[nameof(App.Delay)] = delay,
			[nameof(App.ThrowException)] = throwException,
			[nameof(App.AwaitJsInterop)] = awaitJsInterop,
			[nameof(App.NewWindowUri)] = newWindowUri,
		});

		// Every navigation decision the view makes, captured for scenario 18.
		view.UrlLoading += (_, e) => s_urlLoading.Enqueue($"{e.UrlLoadingStrategy} {e.Url}");

		// Reaching this handler is the failure: it means an exception escaped to the host, which in an app is a crash.
		view.UnhandledException += (_, e) =>
		{
			s_fatal.Enqueue($"UnhandledException: {e.Exception.GetType().Name}: {e.Exception.Message}");
			if (s_verbose)
			{
				Console.WriteLine(e.Exception);
			}

			e.Handled = true;
		};
	}

	private static void WaitFor(Func<bool> condition, TimeSpan timeout, string what)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition())
		{
			if (stopwatch.Elapsed > timeout)
			{
				throw new TimeoutException($"Timed out waiting for {what} after {stopwatch.Elapsed.TotalSeconds:N1}s.");
			}

			PumpOnce();
		}
	}

	/// <summary>A wait that scales with the scenario's own work length.</summary>
	private static TimeSpan FromDelay(int delay, int seconds) =>
		TimeSpan.FromMilliseconds(delay + (seconds * 1000));

	private static HashSet<int> SnapshotBrowserProcesses() =>
		[.. Process.GetProcessesByName("msedgewebview2").Select(p => p.Id)];

	/// <summary>
	/// The WebView2 processes that appeared for this run. Identity is by pid set rather than parent process id: the browser
	/// spawns its children in an order that varies, and only "new since we started a view" is stable.
	/// </summary>
	private static List<Process> WaitForOwnBrowserProcess(HashSet<int> before)
	{
		var processes = new List<Process>();
		WaitFor(
			() =>
			{
				processes.Clear();
				foreach (var process in Process.GetProcessesByName("msedgewebview2"))
				{
					if (!before.Contains(process.Id))
					{
						processes.Add(process);
					}
				}

				return processes.Count > 0;
			},
			TimeSpan.FromSeconds(30),
			"the WebView2 browser process to appear");

		return processes;
	}

	private static string? GetOptionValue(string[] args, string option)
	{
		var index = Array.IndexOf(args, option);
		return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
	}

	/// <summary>Captures error logs. They are reported with the scenario but are not failures by themselves.</summary>
	private sealed class CapturingLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider, ILogger
	{
		public ILogger CreateLogger(string categoryName) => this;

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
			sink.Enqueue(exception is null ? formatter(state, exception) : $"{exception.GetType().Name}: {exception.Message}");

		public void Dispose()
		{
		}
	}

	/// <summary>
	/// Drains the thread's Win32 queue so scenarios can be written as a straight line. The library delivers WebView2
	/// callbacks and queued work through this loop, so a scenario that blocks instead of pumping deadlocks itself.
	/// </summary>
	private static class Pump
	{
		public static void For(TimeSpan duration)
		{
			var stopwatch = Stopwatch.StartNew();
			while (stopwatch.Elapsed < duration)
			{
				PumpOnce();
			}
		}
	}

	private static void PumpOnce()
	{
		var any = false;
		while (Functions.PeekMessageW(out var message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE).Value != 0)
		{
			any = true;
			_ = Functions.TranslateMessage(in message);
			_ = Functions.DispatchMessageW(in message);
		}

		if (!any)
		{
			// Nothing to do: yield without burning a core. Work arriving from another thread posts WM_APP and wakes us.
			Thread.Sleep(5);
		}
	}
}
