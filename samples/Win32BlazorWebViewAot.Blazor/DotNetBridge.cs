using System;
using System.Runtime.InteropServices;
using Microsoft.JSInterop;

namespace Win32BlazorWebViewAot.Blazor
{
	/// <summary>
	/// The smallest JavaScript-to-.NET call there is: one static method a page reaches with
	/// <c>DotNet.invokeMethodAsync('Win32BlazorWebViewAot.Blazor', 'GetAppInfo')</c>, with no component and no
	/// <see cref="IJSRuntime"/> involved.
	/// </summary>
	/// <remarks>
	/// <para>
	/// This is the other direction from what <c>Pages/Home.razor</c> does. A component calls JavaScript through
	/// <see cref="IJSRuntime"/>; anything on the page — inline script, a vendored library, a bookmarklet — calls into .NET
	/// through the global <c>DotNet</c> that <c>_framework/blazor.webview.js</c> installs. Both need the target to carry
	/// <see cref="JSInvokableAttribute"/> and both name the declaring assembly as JS's first argument.
	/// </para>
	/// <para>
	/// Only <c>invokeMethodAsync</c> works here. The synchronous <c>DotNet.invokeMethod</c> is a Blazor WebAssembly feature; on
	/// this host it throws "isn't supported", so even a plain string like the one below arrives as a resolved promise.
	/// </para>
	/// <para>
	/// Discovery is reflection-based, which means trimming and Native AOT drop these methods unless something roots them —
	/// <c>Program.Main</c> does that with a <see cref="System.Diagnostics.CodeAnalysis.DynamicDependencyAttribute"/>. Get it
	/// wrong and the build stays green while every call answers "not found".
	/// </para>
	/// </remarks>
	public static class DotNetBridge
	{
		/// <summary>Set by <c>Program</c> so an arriving call is visible in the console; that log line is how this sample is verified.</summary>
		public static Action<string>? Log { get; set; }

		/// <summary>Raised for the component that shows the call log, so everything on screen is rendered by Blazor.</summary>
		public static event Action<string>? InfoReturned;

		/// <summary>Returns a fact only .NET knows, to prove the value crossed the boundary rather than being invented in JS.</summary>
		[JSInvokable(nameof(GetAppInfo))]
		public static string GetAppInfo()
		{
			Log?.Invoke("GetAppInfo");

			var info = $".NET {Environment.Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";
			InfoReturned?.Invoke(info);

			return info;
		}

		/// <summary>Reports a failed call — a typo in the assembly or method name, or a click before Blazor attached.</summary>
		[JSInvokable(nameof(ReportBridgeFailure))]
		public static void ReportBridgeFailure(string message)
		{
			Log?.Invoke($"GetAppInfo failed: {message}");
			InfoReturned?.Invoke($"call failed: {message}");
		}
	}
}
