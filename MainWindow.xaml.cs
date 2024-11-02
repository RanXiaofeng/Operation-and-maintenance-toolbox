using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Diagnostics;

namespace SimpleToolbox
{
    public partial class MainWindow : Window
    {
        private ToolboxManager _toolboxManager { get; set; } = null!;
        private Dictionary<string, (Button Button, TextBlock StatusText)> _toolButtons { get; set; } = null!;
        private HashSet<string> _runningTools;
        private System.Windows.Threading.DispatcherTimer _resourceMonitorTimer;
        private const int MAX_CONCURRENT_TOOLS = 5;

        public MainWindow()
        {
            InitializeComponent();
            InitializeApplication();
            ShowWelcomeMessage();
            InitializeResourceMonitor();
        }

        private void InitializeApplication()
        {
            _toolboxManager = new ToolboxManager();
            _toolButtons = new Dictionary<string, (Button, TextBlock)>();
            _runningTools = new HashSet<string>();
            TempPathText.Text = _toolboxManager.TempPath;

            InitializeTools();
            ToolManager.ValidateTools();
        }

        private void ShowWelcomeMessage()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MessageBox.Show(
                    "欢迎使用运维工具箱!\n\n" +
                    "建议使用管理运行！！！!\n\n" +
                    "如果您对本软件有任何意见或建议，\n" +
                    "欢迎通过以下方式联系作者：\n\n" +
                    "作者：小峰\n" +
                    "QQ：2634959785\n\n" +
                    "您的反馈将帮助我们把软件做得更好！",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void InitializeResourceMonitor()
        {
            _resourceMonitorTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            _resourceMonitorTimer.Tick += ResourceMonitor_Tick;
            _resourceMonitorTimer.Start();
        }

        private void ResourceMonitor_Tick(object sender, EventArgs e)
        {
            try
            {
                var currentProcess = Process.GetCurrentProcess();
                var memoryUsageMB = currentProcess.WorkingSet64 / (1024 * 1024);

                if (memoryUsageMB > 1024)
                {
                    MessageBox.Show(
                        "工具箱内存使用较高，建议关闭一些不使用的工具。",
                        "内存使用警告",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                }

                CleanupUnresponsiveProcesses();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"资源监控出错: {ex.Message}");
            }
        }

        private void InitializeTools()
        {
            foreach (var tool in ToolManager.Tools)
            {
                var (button, statusText) = CreateToolButton(tool);
                _toolButtons[tool.FileName] = (button, statusText);
                ToolsPanel.Children.Add(button);
            }
        }

        private (Button Button, TextBlock StatusText) CreateToolButton(Tool tool)
        {
            var button = new Button
            {
                Height = 100,
                Width = 180,
                Margin = new Thickness(8)
            };

            button.Style = ToolManager.CreateButtonStyle();

            var stackPanel = new StackPanel
            {
                Margin = new Thickness(0, 12, 0, 12)
            };

            var nameBlock = new TextBlock
            {
                Text = tool.Name,
                FontSize = 15,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.White
            };

            var descriptionBlock = new TextBlock
            {
                Text = tool.Description,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 4, 0, 4)
            };

            var statusBlock = new TextBlock
            {
                Text = "就绪",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.White
            };

            stackPanel.Children.Add(nameBlock);
            stackPanel.Children.Add(descriptionBlock);
            stackPanel.Children.Add(statusBlock);

            button.Content = stackPanel;
            button.Click += ToolButton_Click;

            return (button, statusBlock);
        }

        private async void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button)) return;

            var toolName = ((TextBlock)((StackPanel)button.Content).Children[0]).Text;
            var tool = ToolManager.Tools.FirstOrDefault(t => t.Name == toolName);
            if (tool == null) return;

            if (_runningTools.Count >= MAX_CONCURRENT_TOOLS)
            {
                MessageBox.Show(
                    $"当前运行的工具数量已达到上限({MAX_CONCURRENT_TOOLS})，请先关闭一些工具后再启动新的工具。",
                    "工具数量超限",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            if (_runningTools.Contains(tool.FileName))
            {
                MessageBox.Show(
                    $"{tool.Name} 正在运行中，请等待当前实例完成后再启动新实例。",
                    "提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
                return;
            }

            var (_, statusText) = _toolButtons[tool.FileName];
            await RunToolAsync(button, statusText, tool);
        }

        private async Task RunToolAsync(Button button, TextBlock statusText, Tool tool)
        {
            try
            {
                _runningTools.Add(tool.FileName);
                statusText.Text = "正在运行...";
                UpdateButtonState(button, true);

                await _toolboxManager.RunToolAsync(tool.FileName);
                StartToolMonitoring(tool, button, statusText);
            }
            catch (Exception ex)
            {
                HandleToolExecutionError(ex, statusText);
                _runningTools.Remove(tool.FileName);
                UpdateButtonState(button, false);
            }
        }

        private void CleanupUnresponsiveProcesses()
        {
            foreach (var toolFile in _runningTools.ToList())
            {
                if (_toolboxManager.IsProcessUnresponsive(toolFile))
                {
                    var result = MessageBox.Show(
                        $"工具 \"{toolFile}\" 似乎没有响应。是否结束该进程？",
                        "进程无响应",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning
                    );

                    if (result == MessageBoxResult.Yes)
                    {
                        try
                        {
                            _toolboxManager.KillProcess(toolFile);
                            _runningTools.Remove(toolFile);
                            if (_toolButtons.TryGetValue(toolFile, out var buttonInfo))
                            {
                                UpdateButtonState(buttonInfo.Button, false);
                                buttonInfo.StatusText.Text = "就绪";
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"清理无响应进程失败: {ex.Message}");
                        }
                    }
                }
            }
        }

        private void StartToolMonitoring(Tool tool, Button button, TextBlock statusText)
        {
            Task.Run(async () =>
            {
                try
                {
                    var checkCount = 0;
                    while (_runningTools.Contains(tool.FileName))
                    {
                        var isRunning = _toolboxManager.IsToolRunning(tool.FileName);
                        
                        if (++checkCount % 10 == 0)
                        {
                            var isResponding = !_toolboxManager.IsProcessUnresponsive(tool.FileName);
                            if (!isResponding)
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    statusText.Text = "未响应";
                                    statusText.Foreground = Brushes.Red;
                                });
                            }
                        }

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (!isRunning)
                            {
                                statusText.Text = "就绪";
                                statusText.Foreground = Brushes.White;
                                _runningTools.Remove(tool.FileName);
                                UpdateButtonState(button, false);
                            }
                        });

                        if (!isRunning) break;
                        await Task.Delay(1000);
                    }
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        Debug.WriteLine($"进程监控出错: {ex.Message}");
                        statusText.Text = "就绪";
                        statusText.Foreground = Brushes.White;
                        _runningTools.Remove(tool.FileName);
                        UpdateButtonState(button, false);
                    });
                }
            });

            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(30));
                if (_runningTools.Contains(tool.FileName))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var result = MessageBox.Show(
                            $"工具 \"{tool.Name}\" 已运行超过30分钟，是否要关闭它？",
                            "工具运行时间过长",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question
                        );

                        if (result == MessageBoxResult.Yes)
                        {
                            _toolboxManager.KillProcess(tool.FileName);
                            _runningTools.Remove(tool.FileName);
                            UpdateButtonState(button, false);
                            statusText.Text = "就绪";
                            statusText.Foreground = Brushes.White;
                        }
                    });
                }
            });
        }

        private void UpdateButtonState(Button button, bool isRunning)
        {
            if (isRunning)
            {
                button.IsEnabled = false;
            }
            else
            {
                button.IsEnabled = true;
                button.Style = ToolManager.CreateButtonStyle();
                
                if (button.Content is StackPanel stackPanel)
                {
                    foreach (var child in stackPanel.Children)
                    {
                        if (child is TextBlock textBlock)
                        {
                            textBlock.Foreground = Brushes.White;
                        }
                    }
                }
            }
        }

        private void HandleToolExecutionError(Exception ex, TextBlock statusText)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(
                    $"运行失败: {ex.Message}",
                    "错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                statusText.Text = "运行出错";
            });
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _resourceMonitorTimer?.Stop();

            if (_runningTools.Count == 0)
            {
                _toolboxManager.Cleanup();
                return;
            }

            var result = MessageBox.Show(
                "工具正在运行中，关闭主程序将强制结束工具(可能会出现临时目录删除不完整)。\n\n是否继续？",
                "警告",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (result == MessageBoxResult.Yes)
            {
                _toolboxManager.KillAllProcesses();
                _toolboxManager.Cleanup();
            }
            else
            {
                e.Cancel = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _toolboxManager?.Dispose();
        }
    }
}