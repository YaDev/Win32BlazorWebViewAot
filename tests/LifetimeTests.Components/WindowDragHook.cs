namespace LifetimeTests.Components;

/// <summary>
/// The live window's move entry point, injected by the scenario runner. Scenario 13 drags the harness page's caption
/// strip with real mouse input; its mousedown handler calls whatever is here, which starts the native move loop —
/// exactly how an app wires a borderless title bar (custom chrome).
/// </summary>
public static class WindowDragHook
{
	public static volatile Action? BeginDrag;
}
