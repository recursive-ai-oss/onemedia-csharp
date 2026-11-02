using RecursiveAI.OneMedia.Samples;

// Runs every sample against the small fixtures beside the executable, in a scratch directory, and exits non-zero on the first failure.
string work = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "onemedia-samples-" + Environment.ProcessId);
Directory.CreateDirectory(work);
foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures")))
    File.Copy(file, Path.Combine(work, Path.GetFileName(file)), overwrite: true);

int failed = 0;
foreach (var (name, run) in Tasks.All.Concat(Migration.All))
{
    try { Console.WriteLine($"{name}: {run(work)}"); }
    catch (Exception e) { failed++; Console.WriteLine($"{name}: FAILED {e.GetType().Name}: {e.Message}"); }
}

Directory.Delete(work, recursive: true);
Console.WriteLine(failed == 0 ? "all samples passed" : $"{failed} samples failed");
return failed == 0 ? 0 : 1;
