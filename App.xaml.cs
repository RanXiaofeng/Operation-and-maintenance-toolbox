using System;
using System.Security.Principal;
using System.Windows;

namespace SimpleToolbox
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 检查是否具有管理员权限
            if (!IsRunAsAdministrator())
            {
                MessageBox.Show("请以管理员身份运行此程序！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                Current.Shutdown();
                return;
            }
        }

        private bool IsRunAsAdministrator()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}