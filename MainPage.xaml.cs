namespace BlazecoinWallet.Maui;

public partial class MainPage : ContentPage
{
	public MainPage()
	{
		InitializeComponent();
#if WINDOWS
		// The Send page's QR scanner uses getUserMedia inside the WebView. WebView2
		// blocks camera access unless the host approves the permission request, so
		// auto-grant Camera (loopback desktop wallet; the user explicitly opened the
		// scanner). Without this the getUserMedia call rejects and the scanner falls
		// back to the image-file path. The event-args type is inferred so we don't
		// pin its exact namespace.
		blazorWebView.BlazorWebViewInitialized += (sender, e) =>
		{
			var webView = e.WebView; // Microsoft.UI.Xaml.Controls.WebView2

			void HookPermission()
			{
				var core = webView.CoreWebView2;
				if (core is null) return;
				core.PermissionRequested += (_, args) =>
				{
					if (args.PermissionKind == Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Camera)
						args.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow;
				};
			}

			// CoreWebView2 may or may not be ready by the time this fires.
			if (webView.CoreWebView2 is not null) HookPermission();
			else webView.CoreWebView2Initialized += (_, _) => HookPermission();
		};
#endif
	}
}
