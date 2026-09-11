using Microsoft.Maui.Controls.Shapes;

namespace BlazecoinWallet.Maui;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		// OS window title (taskbar / Alt-Tab) keeps the full product name, plus the
		// profile (Indexer/Pool/Payout) when running as one of several daemon-bound
		// instances, so the windows are tellable apart.
		var profile = MauiProgram.ProfileName;
		var suffix = string.IsNullOrWhiteSpace(profile) ? "" : $" — {profile}";
		var window = new Window(new MainPage()) { Title = $"Blazecoin Wallet V2.0{suffix}" };

		// Pulsing green status dot - slow opacity-only fade, mirroring the
		// V1.5 Qt wallet's QGraphicsOpacityEffect pulse (opacity 0.25 to 1.0,
		// 2400 ms, linear, no scale change).
		var statusDot = new Ellipse
		{
			Fill = Color.FromArgb("#2ECC71"),
			WidthRequest = 9,
			HeightRequest = 9,
			VerticalOptions = LayoutOptions.Center,
			// Measured 1px above the "V2.0" optical centre; nudge down to
			// align. Pulse animates opacity only, so this stays put.
			TranslationY = 1,
			// Tight, subtle glow (was Radius 6 / Opacity 0.9 — too hazy).
			Shadow = new Shadow
			{
				Brush = Color.FromArgb("#2ECC71"),
				Radius = 3,
				Opacity = 0.4f,
				Offset = new Point(0, 0)
			}
		};

		statusDot.Loaded += (_, _) =>
		{
			// Mirrors V1.5's QGraphicsOpacityEffect pulse: opacity dips to 0.25,
			// swells to full at the half-point, eases back - one 2.4 s linear loop.
			var pulse = new Animation();
			pulse.Add(0.0, 0.5, new Animation(v => statusDot.Opacity = v, 0.25, 1.0));
			pulse.Add(0.5, 1.0, new Animation(v => statusDot.Opacity = v, 1.0, 0.25));
			pulse.Commit(statusDot, "blzStatusPulse", length: 2400, repeat: () => true);
		};

		var versionLabel = new Label
		{
			Text = string.IsNullOrWhiteSpace(profile) ? "V2.0" : $"V2.0 · {profile}",
			TextColor = Color.FromArgb("#9E000F"),   // V1.5 version-label crimson (sampled)
			FontAttributes = FontAttributes.Bold,
			FontSize = 13,
			VerticalOptions = LayoutOptions.Center
		};

		var leading = new HorizontalStackLayout
		{
			Spacing = 8,
			Margin = new Thickness(14, 0, 0, 0),
			VerticalOptions = LayoutOptions.Center,
			Children = { statusDot, versionLabel }
		};

		// LeadingContent (left region) — keeps the system min/max/close
		// caption buttons and their passthrough on the far right intact; a
		// full-width custom Content hid them. ForegroundColor stays white so
		// the caption-button glyphs (set in MauiProgram) read white.
		window.TitleBar = new TitleBar
		{
			LeadingContent = leading,
			BackgroundColor = Colors.Black,
			ForegroundColor = Colors.White,
			HeightRequest = 32
		};

		return window;
	}
}
