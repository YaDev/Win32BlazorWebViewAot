using Microsoft.AspNetCore.Components.Web;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// A Razor component mounted at a CSS selector in the host page.
	/// </summary>
	public class RootComponent(string selector, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type componentType, IDictionary<string, object?>? parameters = null)
	{
		/// <summary>The CSS selector of the element the component renders into. Unique among root components.</summary>
		public string Selector { get; } = string.IsNullOrWhiteSpace(selector)
			? throw new ArgumentException("A selector is required.", nameof(selector))
			: selector;

		/// <summary>The component type, which must implement <see cref="IComponent"/>.</summary>
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
		public Type ComponentType { get; } = componentType ?? throw new ArgumentNullException(nameof(componentType));

		/// <summary>Parameters passed to the component, or <see langword="null"/>.</summary>
		public IDictionary<string, object?>? Parameters { get; } = parameters;

		internal Task AddToWebViewManagerAsync(WebViewManager webViewManager) =>
			webViewManager.AddRootComponentAsync(ComponentType, Selector, Parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(Parameters));

		internal Task RemoveFromWebViewManagerAsync(WebViewManager webViewManager) => webViewManager.RemoveRootComponentAsync(Selector);
	}
}
