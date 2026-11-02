using Android.App;
using Android.OS;
using Android.Util;
using RecursiveAI.OneMedia.Smoke;

namespace RecursiveAI.OneMedia.AndroidSmoke;

/// <summary>Runs the shared smoke checks and the phone-app checks once on a worker thread and logs one line, "mobile smoke: ok" or
/// "mobile smoke: FAILED", under the tag OneMediaSmoke, then closes. There is no UI.</summary>
[Activity(Label = "OneMedia smoke", MainLauncher = true)]
public sealed class MainActivity : Activity
{
    private const string Tag = "OneMediaSmoke";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        string storage = FilesDir!.AbsolutePath;
        var thread = new System.Threading.Thread(() =>
        {
            int failures;
            try
            {
                int total = 0;
                int bad = 0;
                void Log(string line)
                {
                    total++;
                    if (line.StartsWith("FAIL", StringComparison.Ordinal))
                    {
                        bad++;
                    }

                    Android.Util.Log.Info(Tag, line);
                }

                failures = SmokeRun.RunAsync(Log).GetAwaiter().GetResult();
                MobileChecks.Run((name, ok) => Log((ok ? "ok   " : "FAIL ") + name), storage);
                Android.Util.Log.Info(Tag, $"android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}) {Build.Model}; {total} check lines, {bad} failed");
                failures = bad;
            }
            catch (Exception e)
            {
                Android.Util.Log.Error(Tag, "unhandled: " + e);
                failures = 1;
            }

            Android.Util.Log.Info(Tag, failures == 0 ? "mobile smoke: ok" : "mobile smoke: FAILED");
            RunOnUiThread(Finish);
        }, 16 * 1024 * 1024);
        thread.Start();
    }
}
