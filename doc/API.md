# API reference

The types you call, and the build switches that change what ships. Plumbing types (`Win32MessageQueue`, `CompositionTarget`,
`SharedSource/*`) are public because the advanced paths need them; they are described in [DESIGN.md](DESIGN.md) rather than here.

## Registration

```csharp
services.AddWin32BlazorWebViewAot();          // returns IWin32BlazorWebViewAotBuilder
services.AddBlazorWebViewDeveloperTools();     // Debug only, normally; see the FAQ
```

| Member | Purpose |
| --- | --- |
| `IWin32BlazorWebViewAotBuilder.UseSharedCoreWebView2Environment(IComObject<ICoreWebView2Environment>)` | share an existing environment across views |
| `IWin32BlazorWebViewAotBuilder.UseSharedCoreWebView2Environment(Func<IServiceProvider, CancellationToken, ValueTask<IComObject<ICoreWebView2Environment>>>)` | create it once, lazily |
| `BlazorWebViewDeveloperTools.Enabled` | whether DevTools and the browser accelerator keys are on. Defaults to `false` |

## Windows

| Type | Frame |
| --- | --- |
| `BlazorWebViewWindow` | native caption, client area is a Blazor app — the usual choice |
| `BorderlessBlazorWebViewWindow` | no frame drawn, page owns every edge |
| `TitleBarWin32Window`, `Win32Window` | window with no view attached |
| `BorderlessWin32Window` | same, borderless |

`Win32Window` is the base and carries everything a window does:

| Member | Notes |
| --- | --- |
| `Show()` `Hide()` `Minimize()` `Maximize()` `Restore()` | `Show()` before `StartAsync()`; the view needs a visible HWND to size its visual |
| `Run()` | pumps messages until the window is destroyed; returns the exit code |
| `Close()` | posts `WM_CLOSE`; shutdown is ordered, see [DESIGN.md](DESIGN.md#lifetime) |
| `BeginDrag()` | starts the native move loop; call while a mouse button is down |
| `ShowSystemMenu(POINT)` | the Alt+Space menu at a screen point |
| `Title` `Handle` `Dpi` `ClientRect` `WindowRect` | rects and DPI are physical pixels |
| `CornerPreference` | `Default`, `DoNotRound`, `Round` (default), `RoundSmall` |
| `ResizeBorderDip` | input band on a borderless window, default 8 DIP |
| `QuitOnDestroy` | default `true`: destroying this window ends `Run()` |
| `IsVisible` `IsClosing` `IsDisposed` | state |
| `static event UnhandledMessageException` | throws inside the window procedure; see [DESIGN.md](DESIGN.md#two-exception-boundaries) |

## The view

`BlazorWebView` is what a window hosts. `BlazorWebViewWindow` exposes the same surface directly, so most apps never construct one.

| Member | Notes |
| --- | --- |
| `Services` `HostPage` `StartPath` | host page is relative to the project, e.g. `wwwroot/index.html` |
| `RootComponents.Add<TComponent>(selector, parameters?)` | mount at a CSS selector; add before or after startup, changes apply live |
| `StartAsync(CancellationToken)` | start it, then call `Run()` — never await before pumping |
| `Dispatcher` | Blazor's dispatcher over the window's message queue |
| `Window` `Controller` `WebView` `CompositionController` | escape hatches; the last three are null until startup completes |
| `AllowDrop` | default `true` |
| `EmbeddedContentAssembly` `EmbeddedContentBaseFolder` | content that lives in another assembly, e.g. a Razor class library |
| `CreateFileProvider(contentRootDir)` | override to serve content from somewhere else entirely |
| `TryDispatchAsync(Action<IServiceProvider>)` | run work on the UI thread; returns false once shutdown started |
| `MoveFocus(reason)` `NotifyParentWindowPositionChanged()` | focus and geometry handoffs to the browser |

Events: `UrlLoading`, `BlazorWebViewInitializing`, `BlazorWebViewInitialized`, `UnhandledException`, `BrowserProcessFailed`.

## Event arguments

| Type | Carries |
| --- | --- |
| `UrlLoadingEventArgs` | `Url`, and `UrlLoadingStrategy` you may rewrite: `OpenInWebView`, `OpenExternally`, `Hide`. Fires for ordinary navigations *and* for new-window requests (`target="_blank"`, `window.open`), which default to `OpenExternally`; on a new-window request `OpenInWebView` means "block it" — the package never opens a second WebView |

External launches (the `OpenExternally` outcome) only shell-execute schemes listed in `WebView2Settings.ExternalLaunchSchemes`
(default `http`, `https`, `mailto`). Anything else — `file:`, `search-ms:`, custom protocol handlers — is refused with a warning
and never reaches the OS loader.
| `BlazorWebViewInitializingEventArgs` | `UserDataFolder`, `BrowserExecutableFolder`, `EnvironmentOptions`, `CoreWebView2Environment` — before the browser exists |
| `BlazorWebViewInitializedEventArgs` | the browser objects, before the first navigation; this is where `WebView2Settings` are overridden |
| `BlazorWebViewUnhandledExceptionEventArgs` | `Exception`, `Handled` |
| `BrowserProcessFailedEventArgs` | the WebView2 failure kind, exit code, and `IsTerminal` |
| `Win32UnhandledExceptionEventArgs` | `Exception`, `Handled`, from the window-procedure boundary |

## MSBuild properties

Set any of these in your project file. Defaults were read out of `build/Win32BlazorWebViewAot.targets` and the library `.csproj`.

| Property | Default | Effect |
| --- | --- | --- |
| `Win32BlazorWebViewAotEmbedContent` | `true` when publishing or building Release, otherwise `false` (`targets:47`) | compile static content into the assembly instead of shipping it beside the exe |
| `Win32BlazorWebViewAotEmbeddedBaseFolder` | `wwwroot` (`targets:51`) | prefix stripped from resource names; must match `BlazorWebView.EmbeddedContentBaseFolder` |
| `Win32BlazorWebViewAotContentRoot` | `wwwroot` (`targets:54`) | folder globbed when a project has no static web assets |
| `Win32BlazorWebViewAotProvideBlazorWebViewJs` | `true` (`targets:119`) | supply `_framework/blazor.webview.js` and `blazor.modules.json` to projects without static web assets |
| `Win32BlazorWebViewAotDisableRdXml` | unset; set `true` to skip | stop rooting the generic JSON converters Blazor's IPC asks for at runtime (`targets:20`) |
| `Win32BlazorWebViewAotEmbedWebView2Loader` | `true` when the WebView2 package is resolvable | embed all three loader architectures in the assembly instead of loose DLLs |

The two properties the package sets on your project — `TrimMode=partial`, `JsonSerializerIsReflectionEnabledByDefault=true` — are
listed with their reasoning in [DESIGN.md](DESIGN.md#what-this-package-changes-about-your-project).
