# FAQ

Symptom first, then the cause. Everything here is behaviour of this library, not of Blazor or WebView2 in general.

## "Cannot receive IPC messages when no page is attached"

Every `[JSInvokable]` call answers this when Blazor never started against your document. Autostart exists to find components and
render them; a plain HTML page has none, so the start fails and the document stays unattached. Load the script with
`autostart="false"` and await `Blazor.start()` before calling.

## A JS call works in Debug and returns "not found" in Release or after publish

The bridge methods are discovered by reflection and nothing references them statically, so the trimmer removes them; the build is
clean and only runtime notices. Root them with `[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(MyBridge))]`
on `Main`.

## The page I published shows HTML I edited out weeks ago

Embedded content is served ahead of files beside the exe — deliberately, so a stale folder cannot shadow what shipped. Either delete
the leftover `wwwroot/` next to the executable or stop embedding with `Win32BlazorWebViewAotEmbedContent=false`.

## Debug edits do not show up

They should: Debug does not embed, and content is read from disk. If an edit is invisible, you are almost certainly running a
Release or published build rather than `dotnet run`, or the file is not under the content root (`Win32BlazorWebViewAotContentRoot`,
default `wwwroot`).

## AOT publish fails inside Blazor's IPC serialization with a project reference

A raw `ProjectReference` does not import the package's `build/*.props` and `*.targets`, which is where the trimming defaults live.
Import both files yourself — what they set is listed in
[DESIGN.md](DESIGN.md#what-this-package-changes-about-your-project).

## `WebView2Loader.dll` was not found

The probe looks in the entry assembly's folder, the executable's own folder, then the copy embedded in this library. A single-file
publish normally finds the embedded one; if you set `Win32BlazorWebViewAotEmbedWebView2Loader=false`, the DLL has to be next to the
exe for the architecture you published.

## The window opens and stays empty, with nothing in the log

No WebView2 Runtime. Check for Evergreen, or point at a fixed version with `BrowserExecutableFolder` during
`BlazorWebViewInitializing` — [distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution).

## F12 and F5 do nothing

In release builds that is the design: the app owns its keyboard, so DevTools and browser accelerator keys are off. They come back in
Debug through `services.AddBlazorWebViewDeveloperTools()`, which this package maps onto the WebView2 user-agent settings that
open DevTools and re-enable the reload keys.

## `StartAsync` never completes

It was awaited before `Run()`. Its continuations are COM callbacks delivered by this thread's message loop, so the pump has to be
running: start it, then call `Run()`.

## Clicking a caption button also moves the window

The mousedown that hit the button reached the drag region too. Stop propagation on the button container, and start the drag
only from `Win32Window.BeginDrag()` in the title-bar handler.

## Work posted after closing throws `Win32DispatcherException`

Expected. Once shutdown starts, queued work faults instead of hanging against a window that no longer pumps. Cancel your producers
when the window reports `IsClosing`.

## An exception in a component surfaces twice, or closes the app

Dispatched failures arrive on `UnhandledException`; set `Handled = true` to keep running, and leave it false if you want a crash
reporter to see it. Throws inside your own `Win32Window.On*` overrides are a different boundary —
[DESIGN.md](DESIGN.md#two-exception-boundaries).

## `DotNet.invokeMethod` throws "isn't supported"

The synchronous form is not implemented on this channel. Use `DotNet.invokeMethodAsync`; even a plain synchronous C# method comes
back as a promise.

## Manual checks

Things automation cannot close, so they are checked by hand on a desktop before a release:

- Native caption mode: snap-layout flyout on caption hover, size cursors over the frame edge, a DPI change while dragging between
  monitors, Alt+Space and Alt+F4.
- The page background reaches all four window edges at 96 / 120 / 144 / 192 DPI, and after maximize.
- IME text composition; touch and pen on real hardware; a file dropped from Explorer; RDP or software rendering.
- Hovering a text input changes the cursor; clicking an `https://…` link opens the system browser.
- Closing the window exits with no finalizer crash and no leftover browser process.
