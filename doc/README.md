# Win32BlazorWebViewAot

Hosts Blazor Razor components — or plain HTML and JS — in a native Win32 window whose content is rendered by
`ICoreWebView2CompositionController`, on the Native-AOT-safe [WebView2Aot](https://www.nuget.org/packages/WebView2Aot) bindings.
No WinForms, no WPF, no `Microsoft.Web.WebView2` managed wrappers. Either with a normal Windows title bar, or borderless so the
page draws its own.

Install from [nuget.org](https://www.nuget.org/packages/Win32BlazorWebViewAot): `dotnet add package Win32BlazorWebViewAot`.

| Read | For |
| --- | --- |
| [START.md](START.md) | installing the package and getting a window on screen; how to build and verify this repo |
| [DESIGN.md](DESIGN.md) | why composition hosting, layer boundaries, startup and shutdown order, threading, and what the package changes about your build |
| [API.md](API.md) | names — the types and main members you call, and the MSBuild properties you can set |
| [FAQ.md](FAQ.md) | it doesn't work, or it works differently in Release than in Debug |

Runnable examples live in `samples/`: Razor app on a native caption (`Win32BlazorWebViewAot.Blazor`), the same app borderless
(`.BlazorBorderless`), plain HTML + JS with `[JSInvokable]` calls both ways (`.Html`, `.HtmlBorderless`).

## External references

These documents describe this library only. Everything it stands on has its own documentation, and that documentation is better
than anything we would write here:

| Topic | Reference |
| --- | --- |
| Blazor Hybrid hosts, and how a desktop host differs from Server or WebAssembly | [ASP.NET Core Blazor Hybrid](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/) |
| Razor component lifecycle and disposal | [lifecycle](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/lifecycle), [disposal](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/component-disposal) |
| JS interop: `[JSInvokable]`, `DotNet.invokeMethodAsync`, `IJSRuntime` | [overview](https://learn.microsoft.com/en-us/aspnet/core/blazor/javascript-interoperability/), [calling .NET from JS](https://learn.microsoft.com/en-us/aspnet/core/blazor/javascript-interoperability/call-dotnet-from-javascript), [`Microsoft.JSInterop`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.jsinterop) |
| Razor class libraries and their `_content/{PACKAGE ID}/…` asset paths | [components in an RCL](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/class-libraries), [hybrid static files](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/static-files) |
| The DI container this library registers into | [Dependency injection in .NET](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview) |
| WebView2 runtime, Evergreen versus fixed version | [distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution), [download](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) |
| The COM interface this library drives directly | [`ICoreWebView2CompositionController`](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2compositioncontroller) |
| Native AOT, trimming, single-file publish | [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/), [preparing libraries for trimming](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming), [single file](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) |
| Rooting reflection-discovered members; source-generated JSON | [`DynamicDependencyAttribute`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.codeanalysis.dynamicdependencyattribute), [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation) |
| Rounded corners and process DPI awareness, which the window layer sets for you | [`DwmSetWindowAttribute`](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmsetwindowattribute), [setting DPI awareness](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process) |
