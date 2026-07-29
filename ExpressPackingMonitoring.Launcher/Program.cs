using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    private const string AppRelativePath = "app\\ExpressPackingMonitoring.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        string baseDirectory = AppContext.BaseDirectory;
        string appPath = Path.Combine(baseDirectory, AppRelativePath);
        if (!File.Exists(appPath))
        {
            MessageBoxW(
                IntPtr.Zero,
                $"未找到主程序：\n{appPath}\n\n请重新下载完整安装包",
                "快递打包监控",
                0x00000010);
            return 2;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appPath,
                WorkingDirectory = Path.GetDirectoryName(appPath)!,
                UseShellExecute = true
            };
            foreach (string argument in args)
                startInfo.ArgumentList.Add(argument);
            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(
                IntPtr.Zero,
                $"启动主程序失败：\n{ex.Message}",
                "快递打包监控",
                0x00000010);
            return 3;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
