# Design

## Why a composition controller

The stock Blazor Hybrid hosts put an HWND-based WebView2 controller inside a WinForms or WPF control: one UI framework dragged in
to own a single child window, and HTML painted inside a frame you cannot remove cleanly. Here the browser is a *visual* in a
composition tree owned by a hand-written window (`WS_EX_NOREDIRECTIONBITMAP`, no redirection surface of its own), so:

- HTML reaches the physical window edge — no client inset under a caption you did not ask for.
- The resize band is an input region only. It costs no rendering space, and hovering it still shows the correct size cursor.
- Nothing in the stack needs reflection-based COM interop, which is what lets the whole thing survive trimming and Native AOT.

The interface being driven is
[`ICoreWebView2CompositionController`](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2compositioncontroller);
the C# bindings come from [WebView2Aot](https://www.nuget.org/packages/WebView2Aot) rather than the `Microsoft.Web.WebView2`
managed wrappers, which are not trim-safe. What a Blazor Hybrid host is in general is covered by
[Microsoft's own page](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/).

## Layers

| Folder | Owns | Knows about Blazor? |
| --- | --- | --- |
| `Window/` | HWND, message loop, frame styles, DPI v2, resize hit-testing, move and system menu | no |
| `Composition/` | compositor controller, root visual, composition controller, mouse/pointer forwarding, cursor sync, drop target | no |
| `WebView/` | the bridge: IPC messages, static content, navigation policy, browser settings, shared environments | yes |
| `Threading/` | message queue posted over `PostMessage`/`WM_APP`, its `SynchronizationContext`, Blazor's `Dispatcher` on top | partly |
| `Services/` | DI registration | yes |
| `Events/` | public event argument types | yes |

You can use `Window/` and `Composition/` with no Razor anywhere — that is what the two HTML samples do.

## Lifetime

Startup: create the window → `Show()` → `StartAsync()` → environment, composition controller, browser, then first navigation.
`StartAsync` awaits COM callbacks that only this thread's message loop delivers, so start it and call `Run()`; awaiting it before
`Run()` never completes.

Shutdown is ordered, and the order is the reason `WM_CLOSE` cannot destroy the window synchronously:

1. Dispose the Razor components and disconnect JS interop — component disposal code runs against a live browser, so this has to
   happen while the browser still exists.
2. Close the browser and release the composition visual.
3. Destroy the HWND.

In-flight interop calls are completed with a cancellation failure at teardown instead of leaking their awaiters; upstream
[dotnet/aspnetcore#70392](https://github.com/dotnet/aspnetcore/issues/70392) hangs them forever. Component disposal itself follows
the usual [Blazor rules](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/component-disposal).

## Threading

`Win32MessageQueue` posts work to the UI thread with `PostMessage` and a `WM_APP` message; `Win32SynchronizationContext` is that
queue seen as a `SynchronizationContext`; Blazor's `Dispatcher` runs on top. Component work therefore lands on the UI thread and
is awaited there. Once shutdown has started, queued work faults with `Win32DispatcherException` rather than hanging against a
window that no longer pumps.

## Two exception boundaries

| Event | Fires for | Notes |
| --- | --- | --- |
| `BlazorWebViewWindow.UnhandledException` / `BlazorWebView.UnhandledException` | an exception thrown by work Blazor dispatched to the UI thread | set `Handled = true` to keep running; otherwise it is promoted to the runtime's unhandled path, which is what a crash reporter expects |
| `Win32Window.UnhandledMessageException` (static) | a throw inside a window procedure — one of your `On*` overrides, or a `Win32MessageQueue.UnhandledException` subscriber failing | Windows called the procedure, so the frames above the throw are native and nothing in `Run()` could catch them. The library catches at that boundary, answers the message with `DefWindowProcW`, and reports here. Process-wide, raised on the UI thread, same `Handled` rule |

A browser process dying is a different thing again: `BrowserProcessFailed` carries `IsTerminal`, so a crashed renderer is
surfaced rather than swallowed.

## What this package changes about your project

Installing the package imports `build/Win32BlazorWebViewAot.props`, which sets two things on **your** project — both only when you
have not set them yourself:

| Property | Default it sets | Why | Override |
| --- | --- | --- | --- |
| `JsonSerializerIsReflectionEnabledByDefault` | `true` (`props:14`) | Blazor's WebView IPC still serializes through reflection-based System.Text.Json | set it to `false` and supply [source-generated contexts](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation) for every type crossing IPC and JS interop |
| `TrimMode` | `partial` (`props:21`) | Razor components are activated and parameterised through reflection; trim only assemblies that opt in with `IsTrimmable` | set your own `TrimMode`, accepting what breaks |

Under `PublishAot=true` it also roots the generic JSON converter instantiations Blazor asks for at runtime, through `build/rd.xml`.
Set `Win32BlazorWebViewAotDisableRdXml=true` once the app supplies source-generated serialization instead.

The native `WebView2Loader.dll` for all three architectures is redistributed under `runtimes/<rid>/native/`, and — because a
published app should be one file — also embedded in this assembly, which is where the loader probe looks after the entry assembly
and the executable's own folder. `Win32BlazorWebViewAotEmbedWebView2Loader=false` ships it as a loose DLL instead.

Content embedding, `_framework/blazor.webview.js` provisioning, and every other knob are in [API.md](API.md#msbuild-properties).

## Why content is embedded

`dotnet publish -c Release -r win-x64 -p:PublishAot=true` produces an exe carrying its own HTML, CSS and JS: no `wwwroot/` beside
it, no DLLs. Static web assets are compiled in as resources named for their site path (`wwwroot.index.html`,
`wwwroot._framework.blazor.webview.js`) and served by an embedded file provider. Taking the list from `@(StaticWebAsset)` rather
than globbing `wwwroot` is what makes it complete — that list also carries `_framework/blazor.webview.js` and
`blazor.modules.json`, the two files a Razor app cannot start without. A plain `Microsoft.NET.Sdk` project has no static web
assets, so its `wwwroot/**` is globbed under the same names instead.

Embedded content is served **ahead** of the files next to the executable, so an old `wwwroot/` left in an output directory cannot
shadow what shipped inside the exe. Debug builds do not embed at all, so editing a page and reloading shows your edit rather than
the bytes baked in at the last build.
