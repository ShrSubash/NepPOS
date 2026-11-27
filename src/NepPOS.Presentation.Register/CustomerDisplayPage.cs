using Microsoft.AspNetCore.Components.WebView.Maui;

namespace NepPOS.Presentation.Register;

/// <summary>The customer-facing window: the same Blazor app, opened straight on the /customer-display page.</summary>
public sealed class CustomerDisplayPage : ContentPage
{
    public CustomerDisplayPage()
    {
        Title = "NepPOS customer display";
        var view = new BlazorWebView { HostPage = "wwwroot/index.html", StartPath = "/customer-display" };
        view.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Components.Routes) });
        view.BlazorWebViewInitialized += (_, e) => e.WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
        Content = view;
    }
}
