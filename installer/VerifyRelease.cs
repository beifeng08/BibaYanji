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
    static void Check(string name, bool passed, object? detail = null)
    {
        Checks.Add(new { name, passed, detail });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {JsonSerializer.Serialize(detail)}");
        if (!passed) throw new InvalidOperationException(name);
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: VerifyRelease <existing-output-directory>"); return 2; }
        Output = Path.GetFullPath(args[0]);
        if (!Directory.Exists(Output)) return 2;
        // Do not start/install a driver in this acceptance run.
        using (var query = new ManagementObjectSearcher("SELECT State FROM Win32_SystemDriver WHERE Name='PawnIO'"))
        using (var rows = query.Get())
        {
            bool running = false;
            foreach (ManagementObject row in rows)
                using (row) running |= string.Equals(row["State"] as string, "Running", StringComparison.OrdinalIgnoreCase);
            if (!running) { Console.Error.WriteLine("PawnIO must already be running; no service action performed."); return 3; }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        ApplyTheme("Light");
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/验机软件;component/Themes/Controls.xaml", UriKind.Relative) });
        app.DispatcherUnhandledException += (_, e) => { Failures.Enqueue(e.Exception.ToString()); e.Handled = true; };
        SensorMonitorService.Sampled += Readings.Enqueue;
        SensorMonitorService.SensorsResolved += map => LastMap = map;
        SensorMonitorService.Failed += Failures.Enqueue;
        Window = new MainWindow { IsHitTestVisible = false, ShowActivated = false };
        Window.Loaded += async (_, _) =>
        {
            try { await Run(); }
            catch (Exception ex) { ExitCode = 1; Checks.Add(new { name = "exception", passed = false, detail = ex.ToString() }); Console.Error.WriteLine(ex); }
            finally
            {
                Window.Close();
                try { await Until(() => !SensorMonitorService.IsRunning, 90); }
                catch (Exception ex) { ExitCode = 1; Failures.Enqueue(ex.ToString()); }
                var samples = Readings.ToArray();
                File.WriteAllText(Path.Combine(Output, "real-monitor-verification.json"), JsonSerializer.Serialize(new
                {
                    date = DateTimeOffset.Now, mode = "real-release-MainWindow-real-hardware-no-stress-load",
                    driver = "already-running-PawnIO", exitCode = ExitCode, checks = Checks,
                    failures = Failures.ToArray(), sampleCount = samples.Length,
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
        return new { count = values.Length, min = values.Length > 0 ? (double?)values.Min() : null,
            max = values.Length > 0 ? (double?)values.Max() : null, mean = values.Length > 0 ? (double?)values.Average() : null };
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
        Window.Close();
        await Until(() => !SensorMonitorService.IsRunning, 120);
        int closed = Readings.Count;
        await Task.Delay(2300);
        Check("window close stops monitoring", Readings.Count == closed);
        Check("no monitoring or dispatcher errors", Failures.IsEmpty, Failures.ToArray());
        Check("no stress workloads started", !StressTestService.IsRunning);
    }
}
