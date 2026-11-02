using System.IO;
using System.Text;
using Foundation;
using UIKit;
using RecursiveAI.OneMedia.Smoke;

namespace RecursiveAI.OneMedia.IosSmoke;

/// <summary>Starts a bare application and runs the shared smoke checks once on a worker thread. The result is one line on the
/// console and in Documents/smoke.txt inside the app's container, "mobile smoke: ok" or "mobile smoke: FAILED", which the run script reads;
/// there is no UI. (The simulator's console capture does not see .NET's console output, hence the file.)</summary>
internal static class AppMain
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
internal sealed class AppDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string resultPath = Path.Combine(documents, "smoke.txt");
        var thread = new System.Threading.Thread(() =>
        {
            var file = new StreamWriter(resultPath, append: false) { AutoFlush = true };
            Console.SetOut(new TeeWriter(Console.Out, file));
            int exit;
            try
            {
                string storage = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                Directory.CreateDirectory(storage);
                int bad = 0;
                int total = 0;
                void Log(string line)
                {
                    total++;
                    if (line.StartsWith("FAIL", StringComparison.Ordinal))
                    {
                        bad++;
                    }

                    Console.WriteLine(line);
                }

                SmokeRun.RunAsync(Log).GetAwaiter().GetResult();
                MobileChecks.Run((name, ok) => Log((ok ? "ok   " : "FAIL ") + name), storage);
                Console.WriteLine($"ios {UIDevice.CurrentDevice.SystemVersion} {UIDevice.CurrentDevice.Model}; {total} check lines, {bad} failed");
                exit = bad == 0 ? 0 : 1;
            }
            catch (Exception e)
            {
                Console.WriteLine("unhandled: " + e);
                exit = 1;
            }

            Console.WriteLine(exit == 0 ? "mobile smoke: ok" : "mobile smoke: FAILED");
            file.Dispose();
            Environment.Exit(exit);
        }, 16 * 1024 * 1024);
        thread.Start();
        return true;
    }
}

/// <summary>Writes everything to two writers, so the result is on the console and in a file.</summary>
internal sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) { first.Write(value); second.Write(value); }
    public override void Write(string? value) { first.Write(value); second.Write(value); }
    public override void WriteLine(string? value) { first.WriteLine(value); second.WriteLine(value); }
    public override void Flush() { first.Flush(); second.Flush(); }
}

/// <summary>Current iOS aborts an app that does not adopt the scene lifecycle, so the app has the smallest scene delegate: one empty window.</summary>
[Register("SceneDelegate")]
internal sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        Window = new UIWindow((UIWindowScene)scene) { RootViewController = new UIViewController() };
        Window.MakeKeyAndVisible();
    }
}
