// Both DirectNAot and WebView2Aot expose a static `Functions` type, so the P/Invoke surfaces are aliased once here
// rather than fully qualified in every file.
global using Native = DirectN.Functions;

global using WebView2Functions = WebView2.Functions;

// DirectNAot projects a COM interface of the same name; DI is what this code means.
global using IServiceProvider = System.IServiceProvider;

// Upstream Blazor WebView host types (WebViewManager and friends). The old nested namespace used to resolve them
// implicitly through its parents; as the flat Win32BlazorWebViewAot root they need an explicit import. Our own// declarations win over these by enclosing-namespace precedence, so the duplicated names (UrlLoadingEventArgs, …) stay ours.
global using Microsoft.AspNetCore.Components.WebView;

// Same story for IComponent: previously found through the enclosing Microsoft.AspNetCore.Components namespace, now
// ambiguous against DirectN's projected COM interface of that name. The alias pins the Blazor one.
global using IComponent = Microsoft.AspNetCore.Components.IComponent;
