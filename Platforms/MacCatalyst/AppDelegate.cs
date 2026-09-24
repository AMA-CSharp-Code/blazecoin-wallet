using System.Runtime.InteropServices;
using Foundation;
using Metal;
using UIKit;

namespace BlazecoinWallet.Maui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	// Dual-GPU Intel MacBook Pros drive the Retina panel with the integrated GPU unless a running
	// app is registered as needing the discrete one. The wallet's animated pages were compositing
	// on the Intel UHD 630 with the Radeon idle (page changes stalled up to 3.9 s; turning automatic
	// graphics switching off in System Settings "fixed it", Andrew 2026-09-24).
	//
	// What did NOT switch the display for this Catalyst app on macOS 26.5 (tested after a reboot):
	//   - NSSupportsAutomaticGraphicsSwitching=false in Info.plist,
	//   - holding a Metal device + command queue on the high-performance GPU.
	// What macOS has always treated as a discrete-GPU dependency is an OpenGL context whose pixel
	// format requires an online accelerated renderer (no kCGLPFAAllowOfflineRenderers). The context
	// is never drawn to; it is simply kept alive for the app's lifetime and released at exit, when
	// macOS switches back. Only attempted on x86_64 Macs that report more than one GPU.
	private static IntPtr _glContext;
	private static IMTLDevice? _highPerformanceGpu;

	private const string OpenGLFramework = "/System/Library/Frameworks/OpenGL.framework/OpenGL";
	private const int kCGLPFAAccelerated = 73;
	private const int kCGLPFANoRecovery = 72;

	[DllImport(OpenGLFramework)] private static extern int CGLChoosePixelFormat(int[] attribs, out IntPtr pix, out int npix);
	[DllImport(OpenGLFramework)] private static extern int CGLCreateContext(IntPtr pix, IntPtr share, out IntPtr ctx);
	[DllImport(OpenGLFramework)] private static extern int CGLDestroyPixelFormat(IntPtr pix);
	[DllImport(OpenGLFramework)] private static extern int CGLDestroyContext(IntPtr ctx);

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
	{
		RequestHighPerformanceGpu();
		return base.FinishedLaunching(application, launchOptions);
	}

	public override void WillTerminate(UIApplication application)
	{
		if (_glContext != IntPtr.Zero)
		{
			try { CGLDestroyContext(_glContext); } catch { /* best effort */ }
			_glContext = IntPtr.Zero;
		}
		base.WillTerminate(application);
	}

	private static void RequestHighPerformanceGpu()
	{
		try
		{
			if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return;   // Apple Silicon: one GPU

			IMTLDevice? discrete = null;
			int gpuCount = 0;
			foreach (var device in MTLDevice.GetAllDevices())
			{
				gpuCount++;
				if (!device.LowPower && !device.Headless) discrete ??= device;
			}
			if (gpuCount < 2 || discrete is null) return;                                // single-GPU Intel Mac
			_highPerformanceGpu = discrete;                                              // keep the device alive too

			var attribs = new[] { kCGLPFAAccelerated, kCGLPFANoRecovery, 0 };
			if (CGLChoosePixelFormat(attribs, out var pix, out var npix) != 0 || pix == IntPtr.Zero || npix == 0)
			{
				Console.WriteLine("GPU: no online accelerated pixel format; not requesting the discrete GPU");
				return;
			}
			var rc = CGLCreateContext(pix, IntPtr.Zero, out _glContext);
			CGLDestroyPixelFormat(pix);
			Console.WriteLine(rc == 0
				? $"GPU: requested the discrete GPU ('{discrete.Name}') via an online OpenGL context"
				: $"GPU: CGLCreateContext failed ({rc}); staying on the default GPU");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"GPU: could not request the high-performance GPU: {ex.Message}");
		}
	}
}
