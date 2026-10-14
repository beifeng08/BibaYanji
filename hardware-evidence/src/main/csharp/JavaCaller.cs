using System.Diagnostics;
using System.Text;

namespace ContractCaller;

public static class JavaCaller
{
    private static readonly TimeSpan NaturalExitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupLimit = TimeSpan.FromSeconds(3);

    // 功能：准备任务、启动 Java、采集日志并分类。背景：方法返回值不能代替 OS 进程退出码。
    // 方法：逐项传参，在等待前启动双管道异步读取；自然退出才保存 ExitCode。
    public static async Task<CallerResult> RunAsync(string[] args)
    {
        var result = new CallerResult();
        CallerConfiguration configuration;
        try
        {
            configuration = CallerConfiguration.FromArguments(args);
            (result.ExecutionId, result.TaskDirectory) = configuration.CreateTaskDirectory();
        }
        catch (Exception exception)
        {
            result.AddCallerFailure(Describe("配置或任务目录准备失败", exception));
            return result;
        }

        using var process = new Process();
        using var readCancellation = new CancellationTokenSource();
        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        bool started = false;
        bool naturalExitObserved = false;

        try
        {
            process.StartInfo = CreateStartInfo(configuration, result.ExecutionId!, result.TaskDirectory!);
            if (!process.Start())
            {
                throw new InvalidOperationException("Process.Start 未成功创建本次子进程。");
            }

            started = true;
            var elapsed = Stopwatch.StartNew();
            stdoutTask = process.StandardOutput.ReadToEndAsync(readCancellation.Token);
            ObserveLateFault(stdoutTask);
            stderrTask = process.StandardError.ReadToEndAsync(readCancellation.Token);
            ObserveLateFault(stderrTask);

            // 从启动后计时；读取的启动开销也计入十秒自然结束期限。
            TimeSpan remaining = NaturalExitLimit - elapsed.Elapsed;
            using var exitCancellation = new CancellationTokenSource();
            if (remaining <= TimeSpan.Zero)
            {
                exitCancellation.Cancel();
            }
            else
            {
                exitCancellation.CancelAfter(remaining);
            }

            try
            {
                await process.WaitForExitAsync(exitCancellation.Token);
                naturalExitObserved = true;
                result.JavaExitCode = process.ExitCode;
                result.Status = result.JavaExitCode switch
                {
                    0 => CallerStatus.Validated,
                    2 => CallerStatus.RequestInvalid,
                    _ => CallerStatus.JavaFailure
                };
            }
            catch (OperationCanceledException) when (exitCancellation.IsCancellationRequested)
            {
                result.Status = CallerStatus.Timeout;
                result.JavaExitCode = null;
                result.AddError("Java 未在启动后的 10 秒期限内自然结束；超时收尾退出码不作为 Java 自然退出码。");
            }
        }
        catch (Exception exception)
        {
            result.AddCallerFailure(Describe(started ? "启动后读取或等待失败" : "启动失败", exception));
        }

        if (started && !naturalExitObserved)
        {
            await StopOwnedProcessAsync(process, result);
        }

        await CollectLogsAsync(process, stdoutTask, stderrTask, readCancellation, result);
        return result;
    }

    private static ProcessStartInfo CreateStartInfo(
        CallerConfiguration configuration, string executionId, string taskDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = configuration.JavaExecutable,
            WorkingDirectory = taskDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        info.ArgumentList.Add("-cp");
        info.ArgumentList.Add(configuration.ClassDirectory);
        info.ArgumentList.Add("DemoCommand");
        info.ArgumentList.Add("demo");
        info.ArgumentList.Add("--execution-id");
        info.ArgumentList.Add(executionId);
        info.ArgumentList.Add("--out");
        info.ArgumentList.Add(taskDirectory);
        return info;
    }

    // 功能：停止本次拥有的子进程。背景：超时并不保证停止成功，不能按进程名批量杀进程。
    // 方法：仅使用本次 Process，记录 Kill 和有界确认等待的实际结果，不重试。
    private static async Task StopOwnedProcessAsync(Process process, CallerResult result)
    {
        try
        {
            if (process.HasExited)
            {
                result.AddError("收尾时观察到本次进程已结束，未请求 Kill；原分类保留。");
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            result.AddCallerFailure(Describe("请求停止本次进程失败；不能确认其已停止", exception));
        }

        using var stopCancellation = new CancellationTokenSource(CleanupLimit);
        try
        {
            await process.WaitForExitAsync(stopCancellation.Token);
            result.AddError("收尾等待已确认本次进程退出；未验证所有后代进程的退出状态。");
        }
        catch (OperationCanceledException) when (stopCancellation.IsCancellationRequested)
        {
            result.AddCallerFailure("停止后的 3 秒确认等待超时；不能确认本次进程已停止。");
        }
        catch (Exception exception)
        {
            result.AddCallerFailure(Describe("停止后的确认等待失败", exception));
        }
    }

    // 功能：有界收齐两路日志。背景：进程退出后，继承管道的后代仍可能阻止 EOF。
    // 方法：两路共同最多等待三秒；超限取消并关闭读端，保留已完整取得的一路。
    private static async Task CollectLogsAsync(
        Process process,
        Task<string>? stdoutTask,
        Task<string>? stderrTask,
        CancellationTokenSource readCancellation,
        CallerResult result)
    {
        var tasks = new List<Task<string>>(2);
        if (stdoutTask is not null)
        {
            tasks.Add(stdoutTask);
        }

        if (stderrTask is not null)
        {
            tasks.Add(stderrTask);
        }

        if (tasks.Count == 0)
        {
            return;
        }

        Task<string[]> allLogs = Task.WhenAll(tasks);
        ObserveLateFault(allLogs);
        try
        {
            await allLogs.WaitAsync(CleanupLimit);
        }
        catch (TimeoutException)
        {
            result.AddCallerFailure("两路日志收尾超过 3 秒；未完成一路返回空文本，日志可能不完整。");
            try
            {
                readCancellation.Cancel();
            }
            catch (Exception exception)
            {
                result.AddCallerFailure(Describe("取消日志读取失败", exception));
            }

            CloseReader(process, isStdout: true, result);
            CloseReader(process, isStdout: false, result);
        }
        catch (Exception exception)
        {
            result.AddCallerFailure(Describe("日志读取失败，日志可能不完整", exception));
        }

        result.Stdout = CompletedLog(stdoutTask, "stdout", result);
        result.Stderr = CompletedLog(stderrTask, "stderr", result);
    }

    private static string CompletedLog(Task<string>? task, string label, CallerResult result)
    {
        if (task is null)
        {
            return string.Empty;
        }

        if (task.IsCompletedSuccessfully)
        {
            return task.GetAwaiter().GetResult();
        }

        if (task.IsFaulted)
        {
            result.AddCallerFailure(Describe($"{label} 读取任务异常", task.Exception!.Flatten()));
        }
        else if (task.IsCanceled)
        {
            result.AddCallerFailure($"{label} 读取已取消；文本不完整，返回空文本。");
        }
        else
        {
            result.AddCallerFailure($"{label} 读取尚未结束；返回空文本，后续异常由观察器消费。");
        }

        return string.Empty;
    }

    private static void CloseReader(Process process, bool isStdout, CallerResult result)
    {
        try
        {
            if (isStdout)
            {
                process.StandardOutput.Dispose();
            }
            else
            {
                process.StandardError.Dispose();
            }
        }
        catch (Exception exception)
        {
            result.AddCallerFailure(Describe($"关闭 {(isStdout ? "stdout" : "stderr")} 读端失败", exception));
        }
    }

    // 有界等待结束后仍未完成的任务也注册故障观察，不用无限 await 把超时变成挂起。
    private static void ObserveLateFault(Task task)
    {
        _ = task.ContinueWith(
            faulted => { _ = faulted.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string Describe(string action, Exception exception) =>
        $"{action}：{exception.GetType().Name}: {exception.Message}";
}
