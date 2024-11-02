using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SimpleToolbox
{
    public class ToolManager
    {
        // 所有可用工具的列表
        public static List<Tool> Tools => new List<Tool>
        {
            new Tool 
            { 
                Name = "geek",
                FileName = "geek.exe",
                Description = "geek卸载工具",
                FolderName = "geek"
            },
            new Tool 
            { 
                Name = "HiBit",
                FileName = "HiBitUninstaller.exe",
                Description = "HiBit卸载工具",
                FolderName = "HiBitUninstaller"
            },
            new Tool 
            { 
                Name = "强制解除占用",
                FileName = "IObitUnlocker.exe",
                Description = "强制解除占用",
                FolderName = "IObitUnlocker"
            },
            new Tool 
            { 
                Name = "Dism++",
                FileName = "Dism++x64.exe",
                Description = "Dism++镜像管理",
                FolderName = "Dism++"
            },
            new Tool 
            { 
                Name = "CPU-Z",
                FileName = "CPU-Z.exe",
                Description = "CPU-Z",
                FolderName = "CPU-Z"
            },
            new Tool 
            { 
                Name = "AIDA64",
                FileName = "AIDA64.exe",
                Description = "查看系统信息",
                FolderName = "AIDA64"
            },
            
            new Tool 
            { 
                Name = "系统修复",
                FileName = "FixWin.exe",
                Description = "FixWin强大的修复工具",
                FolderName = "FixWin"
            },
            new Tool 
            { 
                Name = "硬盘检测",
                FileName = "CrystalDiskInfo.exe",
                Description = "硬盘检测工具",
                FolderName = "CrystalDiskInfo"
            },
            new Tool 
            { 
                Name = "系统转换",
                FileName = "Conversion.exe",
                Description = "一键转换系统版本",
                FolderName = "Conversion"
            },
            
            new Tool 
            { 
                Name = "关闭自带杀毒",
                FileName = "dControl.exe",
                Description = "关闭自带杀毒",
                FolderName = "dControl"
            },
            new Tool 
            { 
                Name = "关闭系统更新",
                FileName = "Wub.exe",
                Description = "关闭系统更新",
                FolderName = "Wub"
            },
            new Tool 
            { 
                Name = "系统激活",
                FileName = "HEUkms.exe",
                Description = "系统激活",
                FolderName = "HEUkms"
            },
            
        };

        // 验证所有工具资源是否存在
        public static void ValidateTools()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resources = assembly.GetManifestResourceNames();

            foreach (var tool in Tools)
            {
                var resourceName = $"SimpleToolbox.Resources.{tool.FileName}";
                if (!resources.Any(r => r.EndsWith(tool.FileName)))
                {
                    Debug.WriteLine($"警告: 找不到工具资源 {tool.FileName}");
                }
            }
        }

        // 创建按钮样式
        public static Style CreateButtonStyle()
        {
            var style = new Style(typeof(Button));

            style.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2196F3"))));
            style.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0));

            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(contentPresenter);

            template.VisualTree = border;
            style.Setters.Add(new Setter(Button.TemplateProperty, template));

            // 鼠标悬停效果
            var mouseOverTrigger = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            mouseOverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2"))));
            style.Triggers.Add(mouseOverTrigger);

            // 按下效果
            var pressedTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressedTrigger.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D47A1"))));
            style.Triggers.Add(pressedTrigger);

            // 禁用效果
            var disabledTrigger = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            disabledTrigger.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BDBDBD"))));
            style.Triggers.Add(disabledTrigger);

            return style;
        }
    }
}