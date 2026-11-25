using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace Win32BlazorWebViewAot.HtmlBorderless
{
	/// <summary>
	/// The .NET side of the page's JavaScript: every method here is callable from JS as
	/// <c>DotNet.invokeMethodAsync(assemblyName, 'Name', ...args)</c>, with no Razor and no components anywhere.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Three things have to line up for a call to arrive, which is why they are all spelled out in this sample: the page has
	/// to load <c>_framework/blazor.webview.js</c> (the build supplies it — see <c>Win32BlazorWebViewAot.targets</c>) and start
	/// it with <c>Blazor.start()</c>, the method has to carry <see cref="JSInvokableAttribute"/>, and JS has to name the
	/// assembly that declares it, here <c>Win32BlazorWebViewAot.HtmlBorderless</c>.
	/// </para>
	/// <para>
	/// The methods are found by reflection at startup, so under trimming and Native AOT they survive only if something roots
	/// them. <c>Program.Main</c> does that with a <see cref="System.Diagnostics.CodeAnalysis.DynamicDependencyAttribute"/>;
	/// without it the build stays green and every call fails at runtime with "not found".
	/// </para>
	/// <para>
	/// This sample has two JavaScript-to-.NET channels side by side on purpose. Window chrome goes over
	/// <c>chrome.webview.postMessage</c> — fire-and-forget strings, which is all a drag or minimize needs (see
	/// <c>Program.cs</c>). Anything that wants a typed argument list or a return value comes here instead.
	/// </para>
	/// </remarks>
	public static class DotNetBridge
	{
		/// <summary>The window the page is allowed to talk about; set once by <c>Program</c> at startup.</summary>
		public static Win32Window? Window { get; set; }

		/// <summary>Set by <c>Program</c> so a call that arrives is visible in the console; the samples verify interop this way.</summary>
		public static Action<string>? Log { get; set; }

		/// <summary>
		/// A plain synchronous method, called from JS with <c>invokeMethodAsync</c> like any other. The synchronous
		/// <c>DotNet.invokeMethod</c> is not implemented on this channel — it throws "isn't supported" — so a value that is ready
		/// immediately still arrives as a resolved promise.
		/// </summary>
		[JSInvokable(nameof(GetMachineName))]
		public static string GetMachineName()
		{
			Log?.Invoke("GetMachineName");
			return Environment.MachineName;
		}

		/// <summary>
		/// Arguments in, awaitable result out: the shape to use for anything real. JS passes JSON, which arrives typed.
		/// </summary>
		[JSInvokable(nameof(SumAsync))]
		public static async Task<int> SumAsync(int first, int second)
		{
			Log?.Invoke($"SumAsync({first}, {second})");

			// Deliberately slow: the page stays responsive and its clock keeps ticking while this runs.
			await Task.Delay(250).ConfigureAwait(false);
			return first + second;
		}

		/// <summary>
		/// Several values back in one round trip. A collection of strings serializes without help from a custom converter, which
		/// is what makes it safe under Native AOT — see the note on this class about reflection-based IPC serialization.
		/// </summary>
		[JSInvokable(nameof(GetRuntimeFactsAsync))]
		public static Task<string[]> GetRuntimeFactsAsync()
		{
			Log?.Invoke("GetRuntimeFactsAsync");

			return Task.FromResult(new[]
			{
				$".NET {Environment.Version}",
				$"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})",
				$"{Environment.ProcessorCount} processors, 64-bit process: {Environment.Is64BitProcess}",
				$"UTC now {DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)}",
			});
		}

		/// <summary>
		/// The reason to prefer this channel over <c>postMessage</c>: a typed argument that .NET acts on and reports back on.
		/// Renames the window, then hands JS the caption it actually set — an empty title is refused rather than applied.
		/// </summary>
		[JSInvokable(nameof(SetWindowTitleAsync))]
		public static Task<string> SetWindowTitleAsync(string title)
		{
			Log?.Invoke($"SetWindowTitleAsync({title})");

			if (Window is not { } window)
			{
				return Task.FromResult("No window is attached to the bridge.");
			}

			if (string.IsNullOrWhiteSpace(title))
			{
				return Task.FromResult("A title needs some text.");
			}

			window.Title = title.Trim();
			return Task.FromResult(window.Title);
		}
	}
}
