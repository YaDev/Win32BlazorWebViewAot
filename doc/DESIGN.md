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

The split between `Composition/CompositionWebViewHost.cs` and `WebView/WebView2AotBlazorManager.cs` is deliberate: neither knows
the other's failure mode. Composition hosting can come up while Blazor IPC is broken, or the reverse, and each half can be
diagnosed without reading the other. The manager *borrows* the browser from the host and never disposes it, which keeps COM
ownership single.

What the manager supplies that Microsoft's `WebViewManager` cannot decide for itself: how bytes reach the browser
(`PostWebMessageAsString`), how they arrive (`WebMessageReceived`), and how a request is answered without an HTTP server.

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

`BlazorWebView` owns the three disposable pieces of a view — dispatcher, composition host, WebView manager — and disposes them in
that order. Getting it wrong is how a desktop Blazor app exits with a finalizer crash instead of cleanly, which is why the order is
encoded once in the view rather than left to callers.

`BlazorWebViewWindow` is the window that drives a view from its own messages, and its whole purpose is to make step 1 above
automatic: `OnCloseRequested` returns *deferred*, the view is disposed on the message queue while the browser and the loop are both
still alive, and only then is the HWND destroyed — at which point the base class posts `WM_QUIT` if the application asked for one.
A window you write yourself gets no such help: see [API.md](API.md#windows).

## Threading

`Win32MessageQueue` posts work to the UI thread with `PostMessage` and a `WM_APP` message; `Win32SynchronizationContext` is that
queue seen as a `SynchronizationContext`; Blazor's `Dispatcher` runs on top. Component work therefore lands on the UI thread and
is awaited there. Once shutdown has started, queued work faults with `Win32DispatcherException` rather than hanging against a
window that no longer pumps.

There is one queue per UI thread: several windows on the same thread share it — and therefore share its synchronization context —
rather than each installing their own. Work lands in a concurrent queue and is drained by a message-only window, so continuations
of WebView2 work resume on the thread COM requires.

The dispatcher upholds four rules Blazor depends on: work dispatched *on* the UI thread runs inline; work from another thread is
scheduled; a dropped item faults its task instead of never completing; exceptions reach the owner's unhandled-exception path. The
tempting simplification to avoid is running inline on a completed queue — "inline" there would execute component work against a
renderer and browser that are already being torn down, so a completed queue rejects everything, including dispatches that arrive on
the UI thread itself.

## Two exception boundaries

| Event | Fires for | Notes |
| --- | --- | --- |
| `BlazorWebViewWindow.UnhandledException` / `BlazorWebView.UnhandledException` | a failure that has no awaiter: work this package runs and drops (`async void` continuations posted through the queue, a root component whose mount nobody holds), or a browser process that went away | set `Handled = true` to keep running; otherwise it is promoted to the runtime's unhandled path, which is what a crash reporter expects. Work **you** awaited is not here: `Dispatcher.InvokeAsync` faults your task and stops, because reporting a failure the caller already caught would turn every handled bug into an unhandled report |
| `Win32Window.UnhandledMessageException` (static) | a throw inside a window procedure — one of your `On*` overrides, or a `Win32MessageQueue.UnhandledException` subscriber failing | Windows called the procedure, so the frames above the throw are native and nothing in `Run()` could catch them. The library catches at that boundary, answers the message with `DefWindowProcW`, and reports here. Process-wide, raised on the UI thread, same `Handled` rule |

The split is deliberate. A component exception belongs to Blazor — it reaches your `ErrorBoundary` or the renderer's own logging,
and re-reporting it through this event only produced two accounts of one bug, with a process teardown attached when nobody claimed
it. The event kept its meaning for the failures that would otherwise disappear silently.

A browser process dying is a different thing again: `BrowserProcessFailed` carries `IsTerminal`, so a crashed renderer is
surfaced rather than swallowed.

## Input and drop under composition hosting

Mouse forwarding sends a managed struct per event. Pointer forwarding cannot: the browser reads the whole `POINTER_INFO` itself
from the pointer id, which is why `CompositionPointerHandler` is small — map the message to an event kind, ask the runtime to build
the info for that id, hand it over. Older WebView2 runtimes do not implement the interface that creates a pointer info. When the
runtime refuses, the handler says so instead of swallowing the message, and the window lets `DefWindowProcW` answer it — which is
what promotes the contact to the legacy mouse messages, so touch still reaches the page as clicks. Answering a pointer message with
`0` here would look harmless and leave touch and pen doing nothing at all.

External drag & drop has the same root cause: a composition-hosted browser has no window of its own, so OLE has nothing to hand a
drop to. The host registers itself as the drop target and forwards to
`ICoreWebView2CompositionController3.DragEnter/DragOver/DragLeave/Drop`. It is not competing with the page for drops — it is the
only object that can receive one.

Both are implemented through source-generated COM marshalling (`[GeneratedComClass]` over DirectN's `[GeneratedComInterface]`),
which is what keeps them AOT-safe: no `ComVisible`, no WinForms, no runtime marshaler — the assembly sets
`DisableRuntimeMarshalling`.

## Native text buffers

`Window/NativeText.cs` exists because of a mismatch in DirectN: `PWSTR.From(string)` hands out a pointer to memory that lives in
the managed allocation space, and `PWSTR.Dispose` releases it with a native free. That is not a leak but heap corruption — the
process dies with `STATUS_HEAP_CORRUPTION` (`0xC0000374`), usually at the next unrelated allocation rather than at the offending
call, which makes it one of the worst failures to trace after the fact. Verified standalone: `PWSTR.From` alone survives a full
compaction; disposing what it returned crashes immediately, with no window code involved.

So nothing in this assembly calls `PWSTR.Dispose`. UTF-16 buffers are allocated with `NativeMemory` (plain malloc under
`DisableRuntimeMarshalling`) and freed with the matching `Free`. Windows copies both the class name at registration and the
caption/title at creation, so freeing as soon as the call returns is correct.

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
