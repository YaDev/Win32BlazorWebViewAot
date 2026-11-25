using Microsoft.Extensions.FileProviders;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// Resolves the host page path against the application's content root.
	/// </summary>
	internal static class HostPageLocator
	{
		/// <summary>
		/// Finds the host page and derives the application root from it.
		/// </summary>
		/// <param name="hostPage">An absolute path, or one relative to <paramref name="appRootDir"/>.</param>
		/// <param name="appRootDir">The directory content was published next to (<see cref="AppContext.BaseDirectory"/>).</param>
		/// <param name="embeddedContent">
		/// The provider for content compiled into the assembly, or <see langword="null"/>. A host page that exists only
		/// there still resolves: an executable is allowed to carry its own HTML, and then no file sits next to it.
		/// </param>
		/// <returns>The application root and the host page's full path — the latter virtual when only the provider has it.</returns>
		/// <exception cref="FileNotFoundException">Neither disk nor <paramref name="embeddedContent"/> has the host page.</exception>
		public static (string AppRootDir, string HostPageFullPath) Resolve(string hostPage, string appRootDir, IFileProvider? embeddedContent = null)
		{
			ArgumentException.ThrowIfNullOrWhiteSpace(hostPage);
			ArgumentException.ThrowIfNullOrWhiteSpace(appRootDir);

			var hostPageFullPath = Path.IsPathRooted(hostPage)
				? hostPage
				: Path.GetFullPath(Path.Combine(appRootDir, hostPage));

			if (File.Exists(hostPageFullPath) || HasEmbeddedHostPage(embeddedContent, hostPage, hostPageFullPath, appRootDir))
			{
				return (appRootDir, hostPageFullPath);
			}

			throw new FileNotFoundException(
				$"The host page '{hostPage}' was not found. Looked at '{hostPageFullPath}', and the application assembly " +
				"carries no embedded copy of it. Either publish with the content folder beside the executable, or let the " +
				$"build embed it (Win32BlazorWebViewAotEmbedContent, on by default) so '{EmbeddedContent.DefaultBaseFolder}' is found inside the assembly.",
				hostPageFullPath);
		}

		/// <summary>
		/// Looks for the host page in embedded content, where it sits at the site root rather than under the application
		/// directory: <c>wwwroot/index.html</c> on disk is <c>index.html</c> inside the <c>wwwroot</c> resource folder.
		/// The path relative to the application root is tried too, for an app that embeds below its site root.
		/// </summary>
		private static bool HasEmbeddedHostPage(IFileProvider? embeddedContent, string hostPage, string hostPageFullPath, string appRootDir)
		{
			if (embeddedContent is null)
			{
				return false;
			}

			foreach (var candidate in new[]
			{
				Path.GetFileName(hostPageFullPath),
				Path.GetRelativePath(appRootDir, hostPageFullPath).Replace(Path.DirectorySeparatorChar, '/'),
				hostPage.Replace('\\', '/').TrimStart('/'),
			})
			{
				if (candidate.Length > 0 && embeddedContent.GetFileInfo(candidate).Exists)
				{
					return true;
				}
			}

			return false;
		}
	}
}
