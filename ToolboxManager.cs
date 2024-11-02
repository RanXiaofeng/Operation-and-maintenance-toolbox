using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;

namespace SimpleToolbox
{
    public class ToolboxManager : IDisposable
    {
        private readonly string _tempPath;
        private readonly Dictionary<string, ProcessDetails> _runningProcesses;
        private bool _disposed;
        private readonly object _processLock = new object();

        public string TempPath => _tempPath;

        private class ProcessDetails
        {
            public Process Process { get; set; } = null!;
            public DateTime StartTime { get; set; }
            public int RetryCount { get; set; }
            public int UnresponsiveCount { get; set; }
            public HashSet<int> ChildProcessIds { get; set; } = new HashSet<int>();
        }

        #region P/Invoke Definitions
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint WM_CLOSE = 0x0010;
        #endregion

        public ToolboxManager()
        {
            _tempPath = Path.Combine(Path.GetTempPath(), $"Toolbox_{Guid.NewGuid()}");
            _runningProcesses = new Dictionary<string, ProcessDetails>();
            Directory.CreateDirectory(_tempPath);
            SecureDirectory(_tempPath);
            Debug.WriteLine($"临时目录: {_tempPath}");
            
            ListAllResources();
        }
        public void ListAllResources()
        {
            try
            {
                Debug.WriteLine("所有嵌入的资源:");
                var assembly = Assembly.GetExecutingAssembly();
                var resources = assembly.GetManifestResourceNames();
                
                if (!resources.Any())
                {
                    Debug.WriteLine("- 没有找到任何资源");
                    return;
                }

                foreach (var resource in resources)
                {
                    using var stream = assembly.GetManifestResourceStream(resource);
                    Debug.WriteLine($"- {resource} (大小: {stream?.Length ?? 0} 字节)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"列出资源时出错: {ex.Message}");
            }
        }

        private void SecureDirectory(string path)
        {
            try
            {
                var dirInfo = new DirectoryInfo(path);
                var security = dirInfo.GetAccessControl();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(
                    WindowsIdentity.GetCurrent().Name,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                dirInfo.SetAccessControl(security);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置目录安全性时出错: {ex.Message}");
            }
        }

        public bool IsProcessUnresponsive(string toolFileName)
        {
            lock (_processLock)
            {
                if (!_runningProcesses.TryGetValue(toolFileName, out var processInfo))
                    return false;

                try
                {
                    var process = processInfo.Process;
                    if (process.HasExited)
                        return false;

                    if (!process.Responding)
                    {
                        processInfo.UnresponsiveCount++;
                        return processInfo.UnresponsiveCount >= 5;
                    }
                    
                    processInfo.UnresponsiveCount = 0;
                    return false;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"检查进程响应状态时出错: {ex.Message}");
                    return false;
                }
            }
        }

        public void KillProcess(string toolFileName)
        {
            lock (_processLock)
            {
                if (_runningProcesses.TryGetValue(toolFileName, out var processInfo))
                {
                    try
                    {
                        KillProcessWithRetry(processInfo);
                        _runningProcesses.Remove(toolFileName);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"终止进程失败: {ex.Message}");
                        throw;
                    }
                }
            }
        }

        public async Task RunToolAsync(string toolName, CancellationToken cancellationToken = default)
        {
            var tool = ToolManager.Tools.FirstOrDefault(t => t.FileName == toolName);
            if (tool == null)
            {
                throw new Exception($"找不到工具: {toolName}");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var toolDirectory = Path.Combine(_tempPath, tool.FolderName);
                Directory.CreateDirectory(toolDirectory);

                await ExtractToolResourcesAsync(tool, cancellationToken);

                var targetPath = Path.Combine(toolDirectory, tool.FileName);
                if (!File.Exists(targetPath))
                {
                    throw new FileNotFoundException($"提取的工具文件未找到: {targetPath}");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true,
                    WorkingDirectory = toolDirectory,
                    Verb = "runas"
                };

                var process = new Process { StartInfo = startInfo };
                try 
                {
                    if (!process.Start())
                    {
                        throw new Exception("进程启动失败");
                    }

                    lock (_processLock)
                    {
                        _runningProcesses[tool.FileName] = new ProcessDetails
                        {
                            Process = process,
                            StartTime = DateTime.Now,
                            RetryCount = 0
                        };
                    }
                    
                    StartChildProcessMonitor(tool.FileName);
                    
                    Debug.WriteLine($"进程已启动，ID: {process.Id}, 工具: {tool.FileName}");
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 1223)
                    {
                        throw new Exception("用户取消了管理员权限请求");
                    }
                    throw;
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"运行工具失败: {ex.Message}", ex);
            }
        }

        private void StartChildProcessMonitor(string toolFileName)
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        lock (_processLock)
                        {
                            if (!_runningProcesses.ContainsKey(toolFileName))
                                return;

                            var processInfo = _runningProcesses[toolFileName];
                            if (processInfo.Process.HasExited)
                                return;

                            var childProcesses = GetChildProcessIds(processInfo.Process.Id);
                            foreach (var childId in childProcesses)
                            {
                                processInfo.ChildProcessIds.Add(childId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"监控子进程时出错: {ex.Message}");
                    }

                    await Task.Delay(1000);
                }
            });
        }

        private HashSet<int> GetChildProcessIds(int parentId)
        {
            var children = new HashSet<int>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentId}");
                foreach (ManagementObject proc in searcher.Get())
                {
                    children.Add(Convert.ToInt32(proc["ProcessId"]));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取子进程ID时出错: {ex.Message}");
            }
            return children;
        }

        public bool IsToolRunning(string toolFileName)
        {
            lock (_processLock)
            {
                if (!_runningProcesses.TryGetValue(toolFileName, out var processInfo))
                {
                    return false;
                }

                try
                {
                    if (processInfo.Process.HasExited)
                    {
                        _runningProcesses.Remove(toolFileName);
                        return false;
                    }

                    bool hasRunningChildren = false;
                    foreach (var childId in processInfo.ChildProcessIds)
                    {
                        try
                        {
                            using var childProcess = Process.GetProcessById(childId);
                            if (!childProcess.HasExited)
                            {
                                hasRunningChildren = true;
                                break;
                            }
                        }
                        catch (ArgumentException)
                        {
                            continue;
                        }
                    }

                    return !processInfo.Process.HasExited || hasRunningChildren;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"检查工具状态时出错 {toolFileName}: {ex.Message}");
                    return false;
                }
            }
        }

        public Dictionary<string, bool> GetAllToolsStatus()
        {
            var status = new Dictionary<string, bool>();
            lock (_processLock)
            {
                foreach (var kvp in _runningProcesses)
                {
                    status[kvp.Key] = IsToolRunning(kvp.Key);
                }
            }
            return status;
        }

        public void KillAllProcesses()
        {
            var failedProcesses = new List<string>();

            foreach (var process in _runningProcesses.ToList())
            {
                try
                {
                    KillProcess(process.Key);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"终止进程 {process.Key} 失败: {ex.Message}");
                    failedProcesses.Add(process.Key);
                }
            }

            if (failedProcesses.Any())
            {
                throw new Exception($"以下进程终止失败: {string.Join(", ", failedProcesses)}");
            }
        }

        private void KillProcessWithRetry(ProcessDetails processInfo, int maxRetries = 3)
        {
            var process = processInfo.Process;
            if (process == null || process.HasExited)
                return;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    if (attempt > 0)
                    {
                        Debug.WriteLine($"重试终止进程 {process.Id}，第 {attempt} 次尝试");
                        Thread.Sleep(1000 * attempt);
                    }

                    if (TryGracefulShutdown(process))
                    {
                        Debug.WriteLine($"进程 {process.Id} 已优雅关闭");
                        return;
                    }

                    foreach (var childId in processInfo.ChildProcessIds.ToList())
                    {
                        try
                        {
                            KillProcessById(childId);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"终止子进程 {childId} 失败: {ex.Message}");
                        }
                    }

                    if (TryKillWithWMI(process.Id))
                    {
                        Debug.WriteLine($"进程 {process.Id} 已通过 WMI 终止");
                        return;
                    }

                    if (TryKillWithWin32Api(process.Id))
                    {
                        Debug.WriteLine($"进程 {process.Id} 已通过 Win32 API 终止");
                        return;
                    }

                    if (TryKillWithTaskKill(process.Id))
                    {
                        Debug.WriteLine($"进程 {process.Id} 已通过 taskkill 终止");
                        return;
                    }

                    process.Kill(true);
                    process.WaitForExit(5000);

                    if (process.HasExited)
                    {
                        Debug.WriteLine($"进程 {process.Id} 已成功终止");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                        throw new Exception($"终止进程失败，已重试 {maxRetries} 次", ex);
                    
                    Debug.WriteLine($"终止进程失败，尝试次数: {attempt + 1}, 错误: {ex.Message}");
                }
            }
        }

        private bool TryGracefulShutdown(Process process)
        {
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    GetWindowThreadProcessId(hWnd, out int windowProcessId);
                    if (windowProcessId == process.Id)
                    {
                        PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                    return true;
                }, IntPtr.Zero);

                return process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"优雅关闭失败: {ex.Message}");
                return false;
            }
        }
        
        public void KillRunningProcess()
        {
            var failedProcesses = new List<string>();

            foreach (var kvp in _runningProcesses.ToList())
            {
                try
                {
                    Debug.WriteLine($"正在终止进程: {kvp.Key}");
                    KillProcessWithRetry(kvp.Value);
                    
                    lock (_processLock)
                    {
                        _runningProcesses.Remove(kvp.Key);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"终止进程失败 {kvp.Key}: {ex.Message}");
                    failedProcesses.Add(kvp.Key);
                }
            }

            if (failedProcesses.Any())
            {
                throw new Exception($"以下进程终止失败: {string.Join(", ", failedProcesses)}");
            }
        }

        private bool IsExtension(string part)
        {
            var knownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "exe", "dll", "zip", "log", "ini", "sys", "xml", "ui", 
                "dat", "json", "txt", "cfg", "config", "manifest"
            };
            return knownExtensions.Contains(part);
        }

        private void KillProcessById(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(2000);
                }
            }
            catch (ArgumentException)
            {
                // 进程已经不存在，忽略
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"终止进程 {processId} 失败: {ex.Message}");
                throw;
            }
        }

        private bool TryKillWithWMI(int processId)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}");
                foreach (ManagementObject proc in searcher.Get())
                {
                    proc.InvokeMethod("Terminate", null);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WMI 终止失败: {ex.Message}");
            }
            return false;
        }

        private bool TryKillWithWin32Api(int processId)
        {
            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = OpenProcess(PROCESS_TERMINATE, false, processId);
                if (hProcess == IntPtr.Zero)
                    return false;

                return TerminateProcess(hProcess, 1);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Win32 API 终止失败: {ex.Message}");
                return false;
            }
            finally
            {
                if (hProcess != IntPtr.Zero)
                    CloseHandle(hProcess);
            }
        }

        private bool TryKillWithTaskKill(int processId)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /T /PID {processId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    Verb = "runas"
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                    return false;

                process.WaitForExit(5000);
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Taskkill 终止失败: {ex.Message}");
                return false;
            }
        }

        public void Cleanup()
        {
            try
            {
                KillRunningProcess();

                if (Directory.Exists(_tempPath))
                {
                    Thread.Sleep(100);

                    int retryCount = 0;
                    const int maxRetries = 3;

                    while (retryCount < maxRetries)
                    {
                        try
                        {
                            var files = Directory.GetFiles(_tempPath, "*.*", SearchOption.AllDirectories);
                            
                            foreach (var file in files)
                            {
                                try
                                {
                                    File.SetAttributes(file, FileAttributes.Normal);
                                    File.Delete(file);
                                    Debug.WriteLine($"已删除: {file}");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"删除文件失败 {file}: {ex.Message}");
                                }
                            }

                            foreach (var dir in Directory.GetDirectories(_tempPath))
                            {
                                try
                                {
                                    Directory.Delete(dir, true);
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"删除目录失败 {dir}: {ex.Message}");
                                }
                            }

                            Directory.Delete(_tempPath, true);
                            Debug.WriteLine($"已清理临时目录: {_tempPath}");
                            break;
                        }
                        catch (Exception ex)
                        {
                            retryCount++;
                            if (retryCount >= maxRetries)
                            {
                                Debug.WriteLine($"清理临时目录失败，已重试 {maxRetries} 次: {ex.Message}");
                                throw;
                            }
                            Debug.WriteLine($"清理失败，正在重试 ({retryCount}/{maxRetries}): {ex.Message}");
                            Thread.Sleep(1000 * retryCount);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清理临时目录失败: {ex.Message}");
                throw;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    try
                    {
                        foreach (var processInfo in _runningProcesses.Values)
                        {
                            try
                            {
                                processInfo.Process?.Dispose();
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"处置进程时出错: {ex.Message}");
                            }
                        }
                        _runningProcesses.Clear();
                        Cleanup();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Dispose 过程中出错: {ex.Message}");
                    }
                }
                _disposed = true;
            }
        }

        ~ToolboxManager()
        {
            Dispose(false);
        }

        private async Task ExtractToolResourcesAsync(Tool tool, CancellationToken cancellationToken = default)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var toolResources = assembly.GetManifestResourceNames()
                    .Where(name => name.Contains($".{tool.FolderName}."))
                    .ToList();

                if (!toolResources.Any())
                {
                    throw new Exception($"找不到工具资源: {tool.FolderName}");
                }

                Debug.WriteLine($"找到 {toolResources.Count} 个资源文件，工具: {tool.FolderName}");
                
                var toolDirectory = Path.Combine(_tempPath, tool.FolderName);
                Directory.CreateDirectory(toolDirectory);

                foreach (var resourceName in toolResources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var targetPath = GetTargetPath(resourceName, toolDirectory, tool);
                        Debug.WriteLine($"正在提取: {resourceName} -> {targetPath}");
                        await ExtractFileAsync(assembly, resourceName, targetPath, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"提取资源失败: {resourceName}, 错误: {ex.Message}");
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"提取工具资源失败: {ex.Message}", ex);
            }
        }

        private string GetTargetPath(string resourceName, string toolDirectory, Tool tool)
        {
            try
            {
                string resourcePath = resourceName.Replace("SimpleToolbox.Resources.", "");
                string[] parts = resourcePath.Split('.');
                
                int toolFolderIndex = Array.FindIndex(parts, p => p.Equals(tool.FolderName, StringComparison.OrdinalIgnoreCase));
                if (toolFolderIndex == -1)
                {
                    throw new Exception($"无法在资源路径中找到工具文件夹: {tool.FolderName}");
                }

                if (resourcePath.EndsWith(tool.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(toolDirectory, tool.FileName);
                }

                List<string> pathSegments = new List<string>();
                List<string> fileNameParts = new List<string>();
                bool isProcessingFileName = false;

                for (int i = toolFolderIndex + 1; i < parts.Length; i++)
                {
                    if (!isProcessingFileName)
                    {
                        if (i == parts.Length - 1 || IsExtension(parts[i + 1]))
                        {
                            isProcessingFileName = true;
                            fileNameParts.Add(parts[i]);
                        }
                        else
                        {
                            pathSegments.Add(parts[i]);
                        }
                    }
                    else
                    {
                        fileNameParts.Add(parts[i]);
                    }
                }

                string relativePath = string.Join(Path.DirectorySeparatorChar.ToString(), pathSegments);
                string fileName = string.Join(".", fileNameParts);
                string fullPath = Path.Combine(toolDirectory, relativePath, fileName);

                string? targetDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                return fullPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理路径时出错 {resourceName}: {ex.Message}");
                throw;
            }
        }

        private async Task ExtractFileAsync(Assembly assembly, string resourceName, string targetPath, CancellationToken cancellationToken)
        {
            const int bufferSize = 81920;
            try 
            {
                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        throw new Exception($"无法加载资源: {resourceName}");
                    }

                    using (var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize))
                    {
                        await stream.CopyToAsync(fileStream, bufferSize, cancellationToken);
                    }
                }

                var fileInfo = new FileInfo(targetPath);
                Debug.WriteLine($"提取完成: {fileInfo.Length:N0} 字节");
            }
            catch (Exception ex)
            {
                throw new Exception($"提取文件失败: {ex.Message}", ex);
            }
        }
    }
}