# Start here

## What you need

- .NET 10 SDK. The library targets `net10.0-windows10.0.19041.0`, so your project must target that TFM or later, on Windows.
- A WebView2 Runtime: Evergreen (preinstalled on current Windows 11) or a fixed-version folder you ship. See
  [distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution). The native loader this package
  redistributes is not a browser — without a runtime the window opens and stays empty.
- Any CPU: win-x64, win-x86, win-arm64. Runtime legs are tested on x64 and x86; ARM64 publishes clean but has not been run.


## First window

`Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Win32BlazorWebViewAot;

internal static class Program
{
    [STAThread]                       // WebView2 and the composition tree are thread-affine
    private static int Main()
    {
        var services = new ServiceCollection();
        services.AddWin32BlazorWebViewAot();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();   // F12 opens DevTools, F5 reloads. Release: neither works.
#endif

        using var window = new BlazorWebViewWindow("My App")
        {
            Services = services.BuildServiceProvider(),
            HostPage = "wwwroot/index.html",
        };

        window.RootComponents.Add<App>("#app");      // omit for a plain HTML + JS page

        window.Show();

        // StartAsync waits on COM callbacks that only this thread's message loop can deliver, so start it and
        // then pump. Awaiting it before Run() deadlocks the startup.
        var startup = window.StartAsync();
        var exitCode = window.Run();

        return startup.IsFaulted ? 1 : exitCode;
    }
}
```

`wwwroot/index.html` needs a `<div id="app"></div>` for each `RootComponents.Add`, and the Blazor script tag:

```html
<script src="_framework/blazor.webview.js" autostart="false"></script>
```

A Razor project gets that file from its static web assets; a plain `Microsoft.NET.Sdk` project gets it from this package. Either
way keep `autostart="false"` and call `Blazor.start()` yourself — see the recipe in [RECIPES.md](RECIPES.md#javascript-calling-net).

## Run the samples

```powershell
dotnet run --project samples/Win32BlazorWebViewAot.Blazor            # Razor app, Windows draws the caption
dotnet run --project samples/Win32BlazorWebViewAot.BlazorBorderless  # same app, page draws its own title bar
dotnet run --project samples/Win32BlazorWebViewAot.Html              # plain HTML + JS, native caption
dotnet run --project samples/Win32BlazorWebViewAot.HtmlBorderless    # plain HTML + JS, page-drawn chrome
```
