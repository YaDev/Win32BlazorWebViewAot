namespace LifetimeTests.Components;

/// <summary>
/// Counters the scenario runner waits on. They are static because a scenario's question is "did work that outlived its
/// component end correctly", which cannot be asked of an instance the renderer already dropped.
/// </summary>
public static class BackgroundWorkState
{
	private static int _started;
	private static int _finished;
	private static int _jsStarted;
	private static int _jsFinished;
	private static int _clicks;

	/// <summary>Background work items that began (the component rendered and started its async method).</summary>
	public static int Started => Volatile.Read(ref _started);

	/// <summary>Background work items that ran to their end, whether or not the view was still alive.</summary>
	public static int Finished => Volatile.Read(ref _finished);

	/// <summary>JavaScript interop calls issued and awaiting a reply.</summary>
	public static int JsStarted => Volatile.Read(ref _jsStarted);

	/// <summary>JavaScript interop calls that returned — only reachable while the view is alive.</summary>
	public static int JsFinished => Volatile.Read(ref _jsFinished);

	/// <summary>Renders the harness clicked through real Win32 mouse input — proves clicks reach the page.</summary>
	public static int Clicks => Volatile.Read(ref _clicks);

	/// <summary>
	/// The last failure the component caught, including failures that happened after its view was gone. Not a scenario
	/// failure in itself: the point is that it stayed here instead of escaping to the host's unhandled-exception path.
	/// </summary>
	public static string? LastFailure { get; internal set; }

	/// <summary>Zeroes everything. Call before each scenario so counts mean exactly what that scenario did.</summary>
	public static void Reset()
	{
		Volatile.Write(ref _started, 0);
		Volatile.Write(ref _finished, 0);
		Volatile.Write(ref _jsStarted, 0);
		Volatile.Write(ref _jsFinished, 0);
		Volatile.Write(ref _clicks, 0);
		LastFailure = null;
	}

	internal static int IncrementStarted() => Interlocked.Increment(ref _started);

	internal static int IncrementFinished() => Interlocked.Increment(ref _finished);

	internal static int IncrementJsStarted() => Interlocked.Increment(ref _jsStarted);

	internal static int IncrementJsFinished() => Interlocked.Increment(ref _jsFinished);

	internal static int IncrementClicks() => Interlocked.Increment(ref _clicks);
}
