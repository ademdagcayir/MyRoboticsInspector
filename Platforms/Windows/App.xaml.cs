using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MyRoboticsInspector.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		// WinUI "stowed exception" (0xc000027b) çökmeleri AppDomain handler'ına
		// düşmez ve crash.log'a yazılmaz — burada yakalayıp logla. Handled=true
		// YAPMA: bozuk durumda devam etmek veri yarısı kayıt riskine yol açar;
		// amaç teşhis edilebilir iz bırakmak.
		this.UnhandledException += (_, e) =>
		{
			try
			{
				var path = System.IO.Path.Combine(
					Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "crash.log");
				System.IO.File.AppendAllText(path,
					$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WinUI.UnhandledException\n{e.Message}\n{e.Exception}\n---\n");
			}
			catch { }
		};

		this.InitializeComponent();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
