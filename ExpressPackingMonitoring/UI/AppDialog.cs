using System.Windows;
using System.Windows.Threading;
using ExpressPackingMonitoring.Localization;

namespace ExpressPackingMonitoring.UI;

public enum AppDialogSeverity
{
    Information,
    Warning,
    Error
}

public static class AppDialog
{
    public static void ShowMessage(
        Window? owner,
        string message,
        string title,
        AppDialogSeverity severity = AppDialogSeverity.Information,
        string? buttonText = null)
    {
        InvokeOnUiThread(() =>
        {
            var dialog = new ConfirmDialog(
                message,
                title,
                confirmText: buttonText ?? AppLanguage.Get("确定"),
                isDangerous: false,
                showCancelButton: false,
                severity: severity);
            ShowOwned(dialog, owner);
            return true;
        });
    }

    public static bool Confirm(
        Window? owner,
        string message,
        string title,
        string? confirmText = null,
        string? cancelText = null,
        AppDialogSeverity severity = AppDialogSeverity.Warning,
        bool isDangerous = false)
    {
        return InvokeOnUiThread(() =>
        {
            var dialog = new ConfirmDialog(
                message,
                title,
                confirmText ?? AppLanguage.Get("确定"),
                cancelText ?? AppLanguage.Get("取消"),
                isDangerous,
                showCancelButton: true,
                severity);
            return ShowOwned(dialog, owner);
        });
    }

    private static bool ShowOwned(ConfirmDialog dialog, Window? requestedOwner)
    {
        Window? owner = ResolveOwner(requestedOwner);
        if (owner != null && owner != dialog)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        return dialog.ShowDialog() == true;
    }

    private static Window? ResolveOwner(Window? requestedOwner)
    {
        if (requestedOwner is { IsLoaded: true })
            return requestedOwner;

        Application? application = Application.Current;
        if (application == null)
            return null;

        return application.Windows
                   .OfType<Window>()
                   .FirstOrDefault(window => window.IsActive && window.IsLoaded)
               ?? (application.MainWindow is { IsLoaded: true } mainWindow ? mainWindow : null);
    }

    private static T InvokeOnUiThread<T>(Func<T> action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            return action();
        return dispatcher.Invoke(action);
    }
}
