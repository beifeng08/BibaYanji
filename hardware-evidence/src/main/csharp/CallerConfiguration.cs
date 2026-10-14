namespace ContractCaller;

// 功能：校验三个配置路径。背景：本演示运行在 Windows，不能依靠当前目录补全路径。
// 方法：拒绝非完整绝对路径，再用文件属性确认启动器、class 根目录及入口文件的类型。
public sealed record CallerConfiguration(string JavaExecutable, string ClassDirectory, string RunsRoot)
{
    public static CallerConfiguration FromArguments(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("demo-args-v1 要求 Windows 完整绝对路径。");
        }

        if (args.Length != 3)
        {
            throw new ArgumentException("用法：Caller <javaExecutable> <classDirectory> <runsRoot>；必须提供三项路径。");
        }

        string java = RequireAbsolutePath(args[0], "javaExecutable");
        string classes = RequireAbsolutePath(args[1], "classDirectory");
        string runs = RequireAbsolutePath(args[2], "runsRoot");
        if (!string.Equals(Path.GetFileName(java), "java.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("javaExecutable 必须指向已存在的 java.exe 文件。");
        }

        RequireEntryType(java, mustBeDirectory: false);
        RequireEntryType(classes, mustBeDirectory: true);
        RequireEntryType(Path.Combine(classes, "DemoCommand.class"), mustBeDirectory: false);

        try
        {
            RequireEntryType(runs, mustBeDirectory: true);
        }
        catch (FileNotFoundException)
        {
            // 允许在任务准备阶段创建缺失的 runsRoot。
        }
        catch (DirectoryNotFoundException)
        {
            // 允许同时创建缺失的父目录。
        }

        return new CallerConfiguration(java, classes, runs);
    }

    // 功能：创建本次任务目录。背景：目录既有内容不得被复用或清理。
    // 方法：先确认路径未占用，再创建；仅适用于合同声明的受控串行演示，不声称原子独占。
    public (string ExecutionId, string TaskDirectory) CreateTaskDirectory()
    {
        Directory.CreateDirectory(RunsRoot);
        string executionId = Guid.NewGuid().ToString("N");
        string taskDirectory = Path.GetFullPath(Path.Combine(RunsRoot, "run-" + executionId));
        RequireMissingPath(taskDirectory);
        Directory.CreateDirectory(taskDirectory);
        return (executionId, taskDirectory);
    }

    private static string RequireAbsolutePath(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"{label} 必须是非空 Windows 完整绝对路径。");
        }

        return Path.GetFullPath(value);
    }

    private static void RequireEntryType(string path, bool mustBeDirectory)
    {
        FileAttributes attributes = File.GetAttributes(path);
        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != mustBeDirectory)
        {
            throw new IOException($"路径类型不符，要求{(mustBeDirectory ? "目录" : "文件")}：{path}");
        }
    }

    private static void RequireMissingPath(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        throw new IOException($"任务路径已被文件或目录占用，拒绝复用：{path}");
    }
}
