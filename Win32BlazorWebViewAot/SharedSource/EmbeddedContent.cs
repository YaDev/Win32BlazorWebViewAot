using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.FileProviders;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Serves the site's static content out of resources compiled into an assembly, which is what lets a published
	/// executable carry its own HTML instead of depending on a folder beside it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The build targets (<c>build/Win32BlazorWebViewAot.targets</c>) turn the application's static web assets into
	/// <c>EmbeddedResource</c> items whose logical name is the site path with every separator written as a dot —
	/// <c>wwwroot/index.html</c>, <c>wwwroot._framework.blazor.webview.js</c>. <see cref="EmbeddedFileProvider"/> reads
	/// exactly that shape, dots being how the CLR spells a folder inside a manifest resource name.
	/// </para>
	/// <para>
	/// Probing an assembly for those names is cheap but not free (<see cref="Assembly.GetManifestResourceNames"/> walks
	/// the module), and every view on an app asks the same question, so the answer is kept per assembly and base folder.
	/// </para>
	/// </remarks>
	internal static class EmbeddedContent
	{
		/// <summary>The resource folder the build embeds content under, and the name callers use by default.</summary>
		public const string DefaultBaseFolder = "wwwroot";

		private static readonly ConcurrentDictionary<(Assembly Assembly, string BaseFolder), IFileProvider?> Cache = new();

		/// <summary>
		/// Returns a provider for <paramref name="assembly"/>'s embedded content, or <see langword="null"/> when the
		/// assembly carries none — which is the normal case for an app that keeps its content on disk.
		/// </summary>
		/// <param name="assembly">Assembly to read, or <see langword="null"/> to use the entry assembly.</param>
		/// <param name="baseFolder">Resource folder the content was embedded under, e.g. <c>wwwroot</c>.</param>
		public static IFileProvider? Create(Assembly? assembly, string baseFolder)
		{
			if (string.IsNullOrWhiteSpace(baseFolder))
			{
				return null;
			}

			var target = assembly ?? Assembly.GetEntryAssembly();
			if (target is null)
			{
				return null;
			}

			// EmbeddedFileProvider matches "wwwroot.<path>"; a name that does not start with the folder plus its dot is
			// somebody else's resource, so this probe says "no embedded site" without constructing anything.
			return Cache.GetOrAdd((target, baseFolder), static key =>
			{
				var prefix = key.BaseFolder + ".";
				foreach (var name in key.Assembly.GetManifestResourceNames())
				{
					if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					{
						return new EmbeddedFileProvider(key.Assembly, key.BaseFolder);
					}
				}

				return null;
			});
		}
	}
}
