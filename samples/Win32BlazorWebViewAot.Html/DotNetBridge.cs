using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace Win32BlazorWebViewAot.Html
{
	/// <summary>
	/// The .NET side of the page's JavaScript: every method here is callable from JS as
	/// <c>DotNet.invokeMethod(assemblyName, 'Name')</c> or its async twin, with no Razor and no components anywhere.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Three things have to line up for a call to arrive, which is why they are all spelled out in this sample: the page has
	/// to load <c>_framework/blazor.webview.js</c> (the build supplies it — see
	/// <c>Win32BlazorWebViewAot.targets</c>), the method has to carry <see cref="JSInvokableAttribute"/>, and JS has to name the
	/// assembly that declares it, here <c>Win32BlazorWebViewAot.Html</c>.
	/// </para>
	/// <para>
	/// The methods are found by reflection at startup, so under trimming and Native AOT they survive only if something roots
	/// them. <c>Program.Main</c> does that with a <see cref="System.Diagnostics.CodeAnalysis.DynamicDependencyAttribute"/>;
	/// without it the build stays green and every call fails at runtime with "not found".
	/// </para>
	/// <para>
	/// Static methods only: Blazor hands the call to the type, not to an instance, so there is no object to hold state. What a
	/// page needs from the app is put behind a property set once at startup — see <see cref="Log"/>.
	/// </para>
	/// </remarks>
	public static class DotNetBridge
	{
		/// <summary>Set by <c>Program</c> so a call that arrives is visible in the console; the samples verify interop this way.</summary>
		public static Action<string>? Log { get; set; }

		/// <summary>
		/// A plain synchronous method, called from JS with <c>invokeMethodAsync</c> like any other. The synchronous
		/// <c>DotNet.invokeMethod</c> is not implemented on this channel — it throws "isn't supported" — so a value that is ready
		/// immediately still arrives as a resolved promise. Keeping the C# side synchronous is what matters: nothing here should
		/// block the page for longer than it has to.
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
	}
}
