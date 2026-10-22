using Microsoft.AspNetCore.Components.WebView;

namespace BlazecoinWallet.App.Droid;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
		// Route external http(s) links (e.g. the block-explorer tx/address links) to the
		// SYSTEM BROWSER instead of navigating them inside the wallet's WebView. The app's
		// own UI is served from the virtual host 0.0.0.0, so anything with a real host is
		// external and should leave the app.
		blazorWebView.UrlLoading += OnUrlLoading;
	}

	private static void OnUrlLoading(object? sender, UrlLoadingEventArgs e)
	{
		var uri = e.Url;
		var isExternalWeb = (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
			&& !uri.Host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase)
			&& !uri.IsLoopback;
		if (isExternalWeb)
			e.UrlLoadingStrategy = UrlLoadingStrategy.OpenExternally;
	}
}
