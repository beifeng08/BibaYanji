using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YanJi;
using YanJi.Plugin.ConfigCheck;
using YanJi.Plugin.StressTest;

// Drives the shipped MainWindow/plugin loader and real hardware. Reflection observes chart state only.
internal static class VerifyRelease
{
    static readonly List<object> Checks = [];
    static readonly ConcurrentQueue<SensorMonitorService.Reading> Readings = new();
    static readonly ConcurrentQueue<string> Failures = new();
    static MainWindow Window = null!;
    static string Output = "";
    static int ExitCode;
    static SensorMonitorService.SensorMap? LastMap;
    static readonly Stopwatch Runtime = Stopwatch.StartNew();
    static readonly ConcurrentQueue<double> SampleTimes = new();
    static readonly List<object> ResourceSamples = [];
    static int SoakSeconds;
    static void Check(string name, bool passed, object? detail = null)
    {
        Checks.Add(new { name, passed, detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {JsonSerializer.Serialize(detail)}");
        if (!passed) throw new InvalidOperationException(name);
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length is not (1 or 3)) { Console.Error.WriteLine("Usage: VerifyRelease <existing-output-directory> [--soak-seconds <60..21600>]"); return 2; }
        if (args.Length == 3 && (args[1] != "--soak-seconds" || !int.TryParse(args[2], out SoakSeconds) || SoakSeconds is < 60 or > 21600)) return 2;
        Output = Path.GetFullPath(args[0]);
        if (!Directory.Exists(Output)) return 2;
        using var rawSamples = new StreamWriter(Path.Combine(Output, "samples.ndjson")) { AutoFlush = true };
        // Do not start/install a driver in this acceptance run.
        using (var query = new ManagementObjectSearcher("SELECT State FROM Win32_SystemDriver WHERE Name='PawnIO'"))
        using (var rows = query.Get())
        {
            bool running = false;
            foreach (ManagementObject row in rows)
                using (row) running |= string.Equals(row["State"] as string, "Running", StringComparison.OrdinalIgnoreCase);
            if (!running) { Console.Error.WriteLine("PawnIO must already be running; no service action performed."); return 3; }
        }
        SensorMonitorService.AllowAutomaticDriverStart = false;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ApplyTheme("Light");
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/验机软件;component/Themes/Controls.xaml", UriKind.Relative) });
        app.DispatcherUnhandledException += (_, e) => { Failures.Enqueue(e.Exception.ToString()); e.Handled = true; };
        SensorMonitorService.Sampled += reading =>
        {
            double seconds = Runtime.Elapsed.TotalSeconds;
            Readings.Enqueue(reading);
            SampleTimes.Enqueue(seconds);
            rawSamples.WriteLine(JsonSerializer.Serialize(new { elapsedSeconds = seconds, reading }));
        };
        SensorMonitorService.SensorsResolved += map => LastMap = map;
        SensorMonitorService.Failed += Failures.Enqueue;
        Window = new MainWindow { IsHitTestVisible = false, ShowActivated = false };
        bool started = false;
        Window.Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            try { await Run(); }
            catch (Exception ex) { ExitCode = 1; Checks.Add(new { name = "exception", passed = false, detail = ex.ToString() }); Console.Error.WriteLine(ex); }
            finally
            {
                Window.Close();
                try { await Until(() => !SensorMonitorService.IsRunning, 90); }
                catch (Exception ex) { ExitCode = 1; Failures.Enqueue(ex.ToString()); }
                var samples = Readings.ToArray();
                if (SoakSeconds > 0) File.WriteAllText(Path.Combine(Output, "soak-progress.json"), JsonSerializer.Serialize(new
                { status = ExitCode == 0 ? "completed" : "failed", requestedSeconds = SoakSeconds, totalElapsedSeconds = Runtime.Elapsed.TotalSeconds, sampleCount = samples.Length, exitCode = ExitCode }));
                File.WriteAllText(Path.Combine(Output, "real-monitor-verification.json"), JsonSerializer.Serialize(new
                {
                    date = DateTimeOffset.Now, mode = "real-release-MainWindow-real-hardware-no-stress-load",
                    driver = "already-running-PawnIO", exitCode = ExitCode, checks = Checks,
                    requestedSoakSeconds = SoakSeconds, totalElapsedSeconds = Runtime.Elapsed.TotalSeconds,
                    failures = Failures.ToArray(), sampleCount = samples.Length, resources = ResourceSamples,
                    stats = new { cpuTemperature = Stats(samples.Select(x => x.CpuTemp)), cpuPower = Stats(samples.Select(x => x.CpuPower)),
                        gpuTemperature = Stats(samples.Select(x => x.GpuTemp)), gpuPower = Stats(samples.Select(x => x.GpuPower)) },
                    limitations = new[] { "Battery failure scenarios belong to user acceptance.", "No driver install/start or stress workload performed.", "Probe creates shipped MainWindow; separate executable smoke verifies process entry." }
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(ExitCode);
            }
        };
        app.Run(Window);
        return ExitCode;
    }
    static object Stats(IEnumerable<double?> source)
    {
        var values = source.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        double? mean = values.Length > 0 ? values.Average() : null;
        return new { count = values.Length, min = values.Length > 0 ? (double?)values.Min() : null,
            max = values.Length > 0 ? (double?)values.Max() : null, mean,
            median = values.Length > 0 ? (double?)Median(values) : null,
            populationVariance = mean is double average ? (double?)values.Select(v => Math.Pow(v - average, 2)).Average() : null,
            distinctValues = values.Distinct().Count() };
    }
    static double Median(double[] values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length % 2 == 0 ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2 : ordered[ordered.Length / 2];
    }
    static async Task Soak()
    {
        Nav("stress");
        int initial = Readings.Count;
        await Until(() => Readings.Count >= initial + 2);
        var timer = Stopwatch.StartNew();
        int startingCount = Readings.Count;
        int startingTimeCount = SampleTimes.Count;
        using var process = Process.GetCurrentProcess();
        var previousCpu = process.TotalProcessorTime;
        double previousSeconds = 0;
        int checkpoint = 0;
        while (timer.Elapsed.TotalSeconds < SoakSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.1, Math.Min(10, SoakSeconds - timer.Elapsed.TotalSeconds))));
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            double seconds = timer.Elapsed.TotalSeconds;
            var resource = new { elapsedSeconds = seconds, samples = Readings.Count,
                workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
                handles = process.HandleCount, threads = process.Threads.Count,
                cpuPercentOfMachine = (cpu - previousCpu).TotalSeconds / (seconds - previousSeconds) / Environment.ProcessorCount * 100 };
            ResourceSamples.Add(resource);
            previousCpu = cpu; previousSeconds = seconds;
            if (!Failures.IsEmpty) throw new InvalidOperationException(string.Join("\n", Failures));
            if (!SensorMonitorService.IsRunning || SampleTimes.IsEmpty || Runtime.Elapsed.TotalSeconds - SampleTimes.Last() > 30)
                throw new InvalidOperationException("No recent monitor sample within 30 seconds");
            if (StressTestService.IsRunning) throw new InvalidOperationException("Unexpected stress workload");
            if ((int)(seconds / 300) > checkpoint)
            {
                checkpoint = (int)(seconds / 300);
                Console.WriteLine($"SOAK {seconds:F0}s samples={Readings.Count} privateMiB={process.PrivateMemorySize64 / 1048576.0:F1}");
            }
            File.WriteAllText(Path.Combine(Output, "soak-progress.json"), JsonSerializer.Serialize(new
            {
                status = "running", requestedSeconds = SoakSeconds, elapsedSeconds = seconds,
                sampleCount = Readings.Count, lastSampleAgeSeconds = Runtime.Elapsed.TotalSeconds - SampleTimes.Last(), resource
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        var times = SampleTimes.Skip(startingTimeCount).ToArray();
        double maxGap = times.Length > 1 ? times.Zip(times.Skip(1), (a, b) => b - a).Max() : double.PositiveInfinity;
        Check("soak requested wall duration reached", timer.Elapsed.TotalSeconds >= SoakSeconds, timer.Elapsed.TotalSeconds);
        Check("soak periodic samples sustained", Readings.Count - startingCount >= SoakSeconds / 5, Readings.Count - startingCount);
        Check("soak no sampling gap over 30 seconds", maxGap <= 30, maxGap);
        Check("soak no dispatcher or monitor failures", Failures.IsEmpty);
        Capture("soak-final.png");
    }
    static async Task Until(Func<bool> condition, int seconds = 120)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (!Failures.IsEmpty) throw new InvalidOperationException(string.Join("\n", Failures));
            if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Acceptance condition timed out");
            await Task.Delay(100);
        }
    }
    static void Nav(string id)
    {
        var nav = (ListBox)Window.FindName("NavList");
        nav.SelectedItem = nav.Items.Cast<ListBoxItem>().Single(x => Equals(x.Tag, id));
    }
    static ConfigCheckPage Config => (ConfigCheckPage)((ContentControl)Window.FindName("ContentHost")).Content;
    static StressTestPage Stress => (StressTestPage)((ContentControl)Window.FindName("ContentHost")).Content;
    static bool ConfigReady() => ((ContentControl)Window.FindName("ContentHost")).Content is ConfigCheckPage page
        && ((ItemsControl)page.FindName("ResultsHost")).ItemsSource != null;
    static void ApplyTheme(string name)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; --i)
            if (dicts[i].Source?.OriginalString.EndsWith("Light.xaml") == true || dicts[i].Source?.OriginalString.EndsWith("Dark.xaml") == true) dicts.RemoveAt(i);
        dicts.Add(new ResourceDictionary { Source = new Uri($"/验机软件;component/Themes/{name}.xaml", UriKind.Relative) });
    }
    static void Capture(string name)
    {
        Window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Window.ActualWidth, (int)Window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(Window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(Output, name)); encoder.Save(file);
    }
    static object? ChartField(string chart, string field) => typeof(LineChartControl).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue((LineChartControl)Stress.FindName(chart));
    static async Task Run()
    {
        Check("six production plugins loaded", ((ListBox)Window.FindName("NavList")).Items.Count == 6);
        bool preview = File.Exists(Path.Combine(AppContext.BaseDirectory, "preview-mode"));
        Check("settings and plugin queue use correct data directory", Path.GetFileName(Settings.DataDirectory) == (preview ? "YanJiPreview" : "YanJi"));
        var ops = typeof(YanJi.Services.PluginPackageService).GetProperty("OpsFile", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as string;
        Check("plugin queue follows isolated settings", Path.GetDirectoryName(ops) == Settings.DataDirectory);
        // Change pages while real collection is in progress; no fake sensor/session injection.
        await Task.Delay(100);
        Nav("stress");
        await Task.Delay(100);
        Nav("config");
        await Until(ConfigReady, 180);
        var groups = ((IEnumerable)((ItemsControl)Config.FindName("ResultsHost")).ItemsSource).Cast<InfoGroup>().ToList();
        Check("real configuration resumes and completes", ((Button)Config.FindName("PassButton")).IsEnabled);
        Check("ten report groups present", groups.Count >= 10, groups.Select(x => x.Title));
        Check("public firmware groups populated", new[] { "BIOS", "整机", "主板" }.All(t => groups.Any(g => g.Title == t && g.Entries.Any(e => e.Name != "采集结果"))));
        for (int cycle = 1; cycle <= 3; ++cycle)
        {
            int before = Readings.Count;
            Nav("stress");
            await Until(() => Readings.Count >= before + 4, 120);
            await Task.Delay(200);
            Check($"cycle {cycle} real periodic samples", Readings.Count >= before + 4);
            Check($"cycle {cycle} supported sensor has positive data", Readings.Skip(before).Any(r => r.CpuTemp > 0 || r.GpuTemp > 0));
            foreach (var chartName in new[] { "CpuTempChart", "CpuPowerChart", "GpuTempChart", "GpuPowerChart" })
            {
                double? Expected()
                {
                    var r = Readings.Last();
                    return chartName switch { "CpuTempChart" => r.CpuTemp, "CpuPowerChart" => r.CpuPower, "GpuTempChart" => r.GpuTemp, _ => r.GpuPower };
                }
                await Until(() => Expected() is double value ? Equals(ChartField(chartName, "_current"), value)
                    : ChartField(chartName, "_current") is null && ChartField(chartName, "_unavailableText") is string);
                var points = (ICollection)ChartField(chartName, "_samples")!;
                Check($"cycle {cycle} {chartName} reflects latest real sample", Expected().HasValue ? points.Count >= 4 : points.Count == 0);
            }
            Check($"cycle {cycle} monitoring does not start workloads", !StressTestService.IsRunning);
            if (cycle == 1)
            {
                foreach (var theme in new[] { "Light", "Dark" })
                {
                    ApplyTheme(theme); await Task.Delay(150);
                    Check(theme + " theme tokens applied", Window.TryFindResource("TextBrush") is SolidColorBrush);
                    Capture("monitor-" + theme + ".png");
                }
                var gpuBox = (ComboBox)Stress.FindName("GpuBox");
                if (gpuBox.Items.Count > 1)
                {
                    int original = gpuBox.SelectedIndex;
                    before = Readings.Count;
                    gpuBox.SelectedIndex = original == 0 ? 1 : 0;
                    await Until(() => Readings.Count > before);
                    string selectedName = ((ComboBoxItem)gpuBox.SelectedItem).Content.ToString()!;
                    await Until(() => LastMap?.GpuName == selectedName);
                    Check("real GPU selection switches sensor mapping", LastMap?.GpuName == selectedName);
                    gpuBox.SelectedIndex = original;
                }
            }
            var oldSession = SensorMonitorService.SessionToken;
            Nav("config");
            await Until(() => !SensorMonitorService.IsRunning, 120);
            int stopped = Readings.Count;
            await Task.Delay(2300);
            Check($"cycle {cycle} no samples after page exit", Readings.Count == stopped);
            Check($"cycle {cycle} old session cancelled", oldSession.IsCancellationRequested);
        }
        Nav("stress");
        int restartCount = Readings.Count;
        await Until(() => Readings.Count > restartCount);
        var previousSession = SensorMonitorService.SessionToken;
        SensorMonitorService.Restart();
        int explicitRestart = Readings.Count;
        await Until(() => SensorMonitorService.SessionToken != previousSession && !SensorMonitorService.SessionToken.IsCancellationRequested && Readings.Count >= explicitRestart + 2, 120);
        Check("explicit stop/start replaces session and resumes real sampling", previousSession.IsCancellationRequested);
        if (SoakSeconds > 0) await Soak();
        Window.Close();
        await Until(() => !SensorMonitorService.IsRunning, 120);
        int closed = Readings.Count;
        await Task.Delay(2300);
        Check("window close stops monitoring", Readings.Count == closed);
        Check("no monitoring or dispatcher errors", Failures.IsEmpty, Failures.ToArray());
        Check("no stress workloads started", !StressTestService.IsRunning);
    }
}
