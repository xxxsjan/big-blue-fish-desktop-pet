using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using BigBlueFish.Setup;
using BigBlueFish.Deployment;
using CheckBox = System.Windows.Controls.CheckBox;

namespace BigBlueFish.Uninstaller;

internal static class UninstallApplication
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string? destination, int flags);

    internal static Window CreateUninstallForm(string target)
    {
        var window = new ModernWindow("卸载蓝色大肥鱼", "和鱼鱼暂时告别", "勾选下方选项，可以保留模型和设置，方便以后重新安装。", 590, 350);
        var retain = new CheckBox { Content = "保留模型和设置", IsChecked = false };
        window.Body.Children.Add(retain);
        window.Actions.Children.Add(ModernUI.Button("取消", () => window.Close()));
        window.Actions.Children.Add(ModernUI.Button("卸载", () =>
        {
            try
            {
                bool keepData = retain.IsChecked == true;
                string confirm = keepData
                    ? "将卸载程序，但保留模型和设置在：\n" + Path.Combine(target, "data")
                    : "将卸载程序，并删除模型和设置。此操作不可恢复。";
                if (ModernUI.Choice(window, "确认卸载", confirm, "继续卸载", "返回") != 0) return;
                var temporary = Path.Combine(Path.GetTempPath(), "BigBlueFish-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Environment.ProcessPath!, temporary);
                // UseShellExecute=false so ArgumentList is inherited with the elevated token.
                var start = new ProcessStartInfo(temporary) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
                start.ArgumentList.Add("--uninstall-worker"); start.ArgumentList.Add(target);
                start.ArgumentList.Add(keepData ? "--keep-data" : "--remove-data");
                Process.Start(start); window.Close();
            }
            catch (Exception error) { ModernUI.Error(window, "卸载未完成", error); }
        }, true));
        return window;
    }

    internal static int Run(string[] args)
    {
        ModernUI.EnsureApplication();
        try
        {
            bool worker = args.Length > 0 && args[0] == "--uninstall-worker";
            if (args.Length > 0 && (args.Length < 2 || args[0] is not ("--uninstall" or "--uninstall-worker")))
                throw new ArgumentException("未提供安装目录。");
            var target = InstallationLifecycle.ValidateInstallation(args.Length == 0 ? Path.GetDirectoryName(Environment.ProcessPath!)! : args[1]);
            if (worker)
            {
                // Prefer explicit --keep-data. Only wipe when --remove-data is present without keep.
                bool keepData = args.Contains("--keep-data") || !args.Contains("--remove-data");
                InstallationLifecycle.Uninstall(target, removeData: !keepData);
                MoveFileEx(Environment.ProcessPath!, null, 4);
                string done = keepData
                    ? "蓝色大肥鱼程序已卸载。\n模型和设置已保留在：\n" + Path.Combine(target, "data")
                    : "蓝色大肥鱼已卸载，模型和设置已删除。";
                ModernUI.Choice(null, "蓝色大肥鱼", done, "确定");
                return 0;
            }
            CreateUninstallForm(target).ShowDialog(); return 0;
        }
        catch (Exception error) { ModernUI.Error(null, "蓝色大肥鱼卸载未完成", error); return 1; }
    }
}
