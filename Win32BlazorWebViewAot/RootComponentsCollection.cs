using Microsoft.AspNetCore.Components.Web;

namespace Win32BlazorWebViewAot
{
	/// <summary>
	/// The root components of a <see cref="BlazorWebView"/>. Add to it before or after startup: changes made once the
	/// view is running are applied live.
	/// </summary>
	public sealed class RootComponentsCollection : ObservableCollection<RootComponent>, IJSComponentConfiguration
	{
		/// <summary>Configuration for components that JavaScript creates and removes.</summary>
		public JSComponentConfigurationStore JSComponents { get; } = new();

		/// <summary>Adds a component at <paramref name="selector"/>.</summary>
		public RootComponentsCollection Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TComponent>(string selector, IDictionary<string, object?>? parameters = null)
			where TComponent : IComponent
		{
			Add(new RootComponent(selector, typeof(TComponent), parameters));
			return this;
		}
	}
}
