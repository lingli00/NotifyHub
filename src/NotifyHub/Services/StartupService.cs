using Microsoft.Win32;

namespace NotifyHub.Services;

/// <summary>
/// 开机自启：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 只操作当前用户，不需要管理员权限。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NotifyHub";

    public static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled, LogService log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                log.Warn("自启", "无法打开注册表 Run 键");
                return false;
            }

            if (enabled)
            {
                if (ExecutablePath.Length == 0)
                {
                    log.Warn("自启", "无法取得当前 exe 路径");
                    return false;
                }

                key.SetValue(ValueName, $"\"{ExecutablePath}\"", RegistryValueKind.String);
                log.Info("自启", "已写入开机自启", ExecutablePath);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                log.Info("自启", "已移除开机自启");
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Error("自启", "设置开机自启失败", ex.Message);
            return false;
        }
    }
}
