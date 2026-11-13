using KitLugia.Core;
using System.Runtime;

var locations = new[]
{
    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
    @"D:\",
};

// Amostragem de GC em background durante o scan inteiro.
var sampling = true;
var samples = new List<(DateTime t, double pausePct, long heap, long loh, int gen2)>();
var sampler = new Thread(() =>
{
    while (sampling)
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            samples.Add((DateTime.Now, info.PauseTimePercentage,
                GC.GetTotalMemory(false), info.HeapSizeBytes, GC.CollectionCount(2)));
        }
        catch { }
        Thread.Sleep(500);
    }
}) { IsBackground = true };
sampler.Start();

var sw = System.Diagnostics.Stopwatch.StartNew();
Console.WriteLine("=== PortableAppScanner.Scan (escopo: perfil + D:) ===");
var apps = PortableAppScanner.Scan(
    progress: (d, t) => { },
    ct: CancellationToken.None,
    status: msg => Console.WriteLine($"  [status] {msg}"),
    includeUserProfile: true,
    driveRoots: new[] { @"D:\" });
sw.Stop();
sampling = false;
sampler.Join(2000);

Console.WriteLine($"scan levou {sw.Elapsed.TotalSeconds:N1}s — apps: {apps.Count}");
Console.WriteLine($"amostras GC: {samples.Count}");
if (samples.Count > 0)
{
    double maxPause = samples.Max(s => s.pausePct);
    double avgPause = samples.Average(s => s.pausePct);
    long maxHeap = samples.Max(s => s.heap);
    long maxLoh = samples.Max(s => s.loh);
    int gen2 = samples.Max(s => s.gen2) - samples.Min(s => s.gen2);
    Console.WriteLine($"PauseTimePercentage: max={maxPause:F1}% avg={avgPause:F1}%");
    Console.WriteLine($"heap max={maxHeap / 1048576.0:N0} MB | LOH max={maxLoh / 1048576.0:N0} MB | gen2 GCs={gen2}");
}
return 0;
