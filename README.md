# Win32BlazorWebViewAot

Host [Microsoft Blazor](https://blazor.net) (Razor components) — or plain HTML + JS — in a native Win32 window, on the NativeAOT-safe
[WebView2Aot](https://www.nuget.org/packages/WebView2Aot) bindings. Either with a normal Windows title bar, or borderless
so the page draws its own chrome.

No WinForms. No WPF. No `Microsoft.Web.WebView2` managed wrappers. No reflection-based COM interop. Trim- and
Native-AOT-clean, verified by running the real-window test suite as a Native AOT executable (x64 and x86).

Documentation lives in [`doc/`](doc/): [getting started](doc/START.md), [design](doc/DESIGN.md),
[API reference](doc/API.md), [FAQ](doc/FAQ.md).

## Install

```powershell
dotnet add package Win32BlazorWebViewAot
```

Or in the project file:

```xml
<ItemGroup>
  <PackageReference Include="Win32BlazorWebViewAot" Version="1.0.0" />
</ItemGroup>
```

Target `net10.0-windows10.0.19041.0` or later on Windows; win-x64, win-x86 and win-arm64 all work. Installing the package also
sets two build defaults on your project — [what it changes](doc/DESIGN.md#what-this-package-changes-about-your-project).
[Start here](doc/START.md) for the first window.

## Repository layout

| Path | What it is |
|---|---|
| `Win32BlazorWebViewAot/` | The library, packed as [Win32BlazorWebViewAot](https://www.nuget.org/packages/Win32BlazorWebViewAot) (`dotnet pack`). |
| `doc/` | Documentation: getting started, design, API reference, FAQ. |
| `samples/Win32BlazorWebViewAot.Blazor` | Razor app on a native-caption window. |
| `samples/Win32BlazorWebViewAot.BlazorBorderless` | Same app, borderless: the page draws its own title bar. |
| `samples/Win32BlazorWebViewAot.Html` | Plain HTML + JS (no Razor files) with `[JSInvokable]` calls into .NET, native caption. |
| `samples/Win32BlazorWebViewAot.HtmlBorderless` | Same HTML app, borderless, page-drawn chrome. |
| `tests/Win32BlazorWebViewAot.LifetimeTests` | 24-scenario real-window lifetime harness; exit code is the verdict. |

## Working on this repository

The samples reference the library by `ProjectReference`, so cloning is enough — no package feed involved.

```powershell
dotnet build Win32BlazorWebViewAot.slnx          # everything, Debug
dotnet run --project samples/Win32BlazorWebViewAot.Blazor

# Native AOT single-file publish (exe carries its own HTML)
dotnet publish samples/Win32BlazorWebViewAot.Html -c Release -r win-x64 -p:PublishAot=true

# The lifetime harness (JIT build; also runs as Native AOT — see doc/START.md)
.\tests\Win32BlazorWebViewAot.LifetimeTests\bin\Debug\net10.0-windows10.0.19041.0\Win32BlazorWebViewAot.LifetimeTests.exe
```

Requires .NET 10 SDK and the Evergreen WebView2 Runtime (or a fixed-version folder). Debug builds open Edge DevTools with
F12 and reload with F5; release builds expose neither — the app owns its keyboard.

## License

Licensed under the **MIT license** — see [LICENSE](LICENSE). The managed dependencies are MIT licensed as well; the one
exception is the native `WebView2Loader.dll`, redistributed here under Microsoft's standard BSD-style terms.
