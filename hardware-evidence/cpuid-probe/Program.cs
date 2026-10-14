// 功能：CPUID 原始寄存器采集探针——按 30 §10 候选形状写 cpuid.json
// 背景：L4 候选切片（未冻结）；只管采集与落盘，不做品牌/厂商解码（解码归属候选A/B未定）
// 方法：X86Base.CpuId 采固定叶 → 组 JSON → 临时文件关闭后提交最终名；探针独立 exe，不改 demo-args-v1 契约

using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;

namespace CpuidProbe;

// 功能：单条寄存器记录，对应 30 §10 的六键固定形状
// 背景：leaf/subleaf 不得重复；值保留 32 位位模式
// 方法：全部为 8 位小写十六进制字符串、不带 0x
internal sealed record RegisterEntry(string Leaf, string Subleaf, string Eax, string Ebx, string Ecx, string Edx);

// 功能：诊断条目，对应 30 §12 diagnostics 候选形状
// 背景：sourceId 允许 null（非来源相关诊断）
// 方法：code 稳定标识；severity=info/warning/error；stage=request/collect/parse/check/output/demo
internal sealed record DiagnosticEntry(string Code, string Severity, string Stage, string? SourceId, string Message);

// 功能：source 对象，对应 30 §10 请求根键 source
// 背景：status=captured/notSupported/failed；后两者 registers 为空且至少一条诊断，不伪造全零寄存器
// 方法：sourceId/kind 固定 cpuid
internal sealed record CpuidSource(string SourceId, string Kind, string Status, List<RegisterEntry> Registers, List<DiagnosticEntry> Diagnostics);

// 功能：请求根对象，对应 30 §10 候选形状
// 背景：探针无真实 Java 命令，executionId/captureId 为探针自建的占位身份（Guid N 格式），见 README
// 方法：schemaVersion 整数 1；mode 固定 collect
internal sealed record CpuidRequest(int SchemaVersion, string Mode, string ExecutionId, string CaptureId, CpuidSource Source);

// 功能：探针入口——校验输出目录、执行采集、提交 cpuid.json、打印摘要
// 背景：退出码 0=已采集并提交；1=采集或落盘失败（含最终名已占用）；2=参数/目录不合法
// 方法：参数校验 → Collect → 临时文件 → File.Move 提交 → 控制台 UTF-8 摘要
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]) || !Path.IsPathFullyQualified(args[0]))
        {
            Console.Error.WriteLine("用法：CpuidProbe <输出目录绝对路径>");
            return 2;
        }

        string outputDirectory = args[0];
        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"输出目录不可用：{ex.GetType().Name}: {ex.Message}");
            return 2;
        }

        string finalPath = Path.Combine(outputDirectory, "cpuid.json");
        if (File.Exists(finalPath))
        {
            Console.Error.WriteLine($"拒绝覆盖：{finalPath} 已存在（与契约拒绝复用旧文件一致）");
            return 1;
        }

        CpuidSource source = Collect();
        CpuidRequest request = new(
            SchemaVersion: 1,
            Mode: "collect",
            ExecutionId: Guid.NewGuid().ToString("N"),
            CaptureId: Guid.NewGuid().ToString("N"),
            Source: source);

        string json = JsonSerializer.Serialize(request, JsonOptions) + "\n";
        string tempPath = Path.Combine(outputDirectory, "cpuid.json.tmp");
        try
        {
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, finalPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"落盘失败：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"status={source.Status}");
        Console.WriteLine($"registers={source.Registers.Count}");
        Console.WriteLine($"output={finalPath}");
        return source.Status == "captured" ? 0 : 1;
    }

    // 功能：采集 30 §10 候选规定的固定叶
    // 背景：保留 00000000 与 80000000 的 subleaf=00000000；扩展最大叶允许时读 80000002/3/4
    // 方法：X86Base.IsSupported 守卫；最大叶按无符号语义比较；异常归入 failed 并保留诊断
    private static CpuidSource Collect()
    {
        var registers = new List<RegisterEntry>();
        var diagnostics = new List<DiagnosticEntry>();

        if (!X86Base.IsSupported)
        {
            diagnostics.Add(new DiagnosticEntry("cpuid.notSupported", "error", "collect", "cpuid",
                "当前平台不支持 X86Base.CpuId 内在函数"));
            return new CpuidSource("cpuid", "cpuid", "notSupported", registers, diagnostics);
        }

        try
        {
            Add(registers, 0x00000000, 0);
            var maxExtended = X86Base.CpuId(unchecked((int)0x80000000), 0);
            registers.Add(Entry(maxExtended, "80000000", "00000000"));
            // 无符号语义比较：EAX 作为 uint 与 0x80000004 比，避免符号位误判
            if ((uint)maxExtended.Eax >= 0x80000004u)
            {
                Add(registers, unchecked((int)0x80000002), 0);
                Add(registers, unchecked((int)0x80000003), 0);
                Add(registers, unchecked((int)0x80000004), 0);
            }
            else
            {
                diagnostics.Add(new DiagnosticEntry("cpuid.brandLeavesAbsent", "info", "collect", "cpuid",
                    "扩展最大叶不足 80000004，品牌叶未采集（来源仍可 captured）"));
            }
        }
        catch (Exception ex)
        {
            registers.Clear();
            diagnostics.Add(new DiagnosticEntry("cpuid.collectFailed", "error", "collect", "cpuid",
                $"采集异常：{ex.GetType().Name}: {ex.Message}"));
            return new CpuidSource("cpuid", "cpuid", "failed", registers, diagnostics);
        }

        return new CpuidSource("cpuid", "cpuid", "captured", registers, diagnostics);
    }

    // 功能：按叶/子叶采一条寄存器记录
    // 背景：subleaf 固定 0；六键形状由 Entry 保证
    // 方法：直接调 X86Base.CpuId，leaf 格式化为 8 位小写 hex
    private static void Add(List<RegisterEntry> registers, int leaf, int subleaf)
    {
        var r = X86Base.CpuId(leaf, subleaf);
        registers.Add(Entry(r, Hex(leaf), Hex(subleaf)));
    }

    // 功能：把 CpuId 返回值组装为六键记录
    // 背景：int 寄存器按 uint 保 32 位位模式
    // 方法：四个寄存器各自转 8 位小写 hex
    private static RegisterEntry Entry((int Eax, int Ebx, int Ecx, int Edx) r, string leaf, string subleaf)
        => new(leaf, subleaf, Hex(r.Eax), Hex(r.Ebx), Hex(r.Ecx), Hex(r.Edx));

    // 功能：32 位值转 8 位小写十六进制字符串
    // 背景：契约要求不带 0x、小写、保位模式
    // 方法：强转 uint 后 x8 格式化，负值（高位为 1）不丢位
    private static string Hex(int value) => ((uint)value).ToString("x8");
}
