# Win32BlazorWebViewAot

Hosts Blazor Razor components — or plain HTML and JS — in a native Win32 window, on the Native-AOT-safe WebView2Aot bindings. No WinForms, no WPF, no
`Microsoft.Web.WebView2` managed wrappers, no reflection-based COM interop. Either with a normal Windows title bar, or borderless so
the page draws its own chrome.

Target: `net10.0-windows10.0.19041.0`, win-x64 / win-x86 / win-arm64. Requires the Evergreen WebView2 Runtime or a fixed-version
folder you ship.

```powershell
dotnet add package Win32BlazorWebViewAot
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using Win32BlazorWebViewAot;

var services = new ServiceCollection();
services.AddWin32BlazorWebViewAot();
#if DEBUG
services.AddBlazorWebViewDeveloperTools();   // F12 opens DevTools, F5 reloads. Release: neither.
#endif

using var window = new BlazorWebViewWindow("My App")
{
    Services = services.BuildServiceProvider(),
    HostPage = "wwwroot/index.html",
};

window.RootComponents.Add<App>("#app");
window.Show();
_ = window.StartAsync();   // resumes inside the message loop — do not await before Run()
window.Run();
```

Entry points must be `[STAThread]`. For a borderless window use `BorderlessBlazorWebViewWindow` and drag it from your own title bar
with `Win32Window.BeginDrag()`.

Package page: https://www.nuget.org/packages/Win32BlazorWebViewAot

Documentation: https://github.com/YaDev/Win32BlazorWebViewAot/tree/main/doc — getting started, design, API reference, FAQ.

Licensed under the MIT license; the license text ships in this package as `LICENSE`. Runtime dependencies are MIT-licensed as well,
the one exception being the native `WebView2Loader.dll` redistributed here under Microsoft's standard BSD-style redistribution terms.
