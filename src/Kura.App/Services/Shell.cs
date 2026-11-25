using System.Diagnostics;
using System.Windows;
using Kura.Core.Models;

namespace Kura.App.Services;

/// <summary>Small OS helpers: open links, copy text, show messages through the overlay gate.</summary>
public static class Shell
{
    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !SourceLinks.IsWebLink(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    public static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { } // clipboard busy in another process
    }

    public static void Error(string message, string title = "Kura")
    {
        using (AppServices.Current?.Overlay.Enter())
            MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static bool Confirm(string message, string title = "Kura")
    {
        using (AppServices.Current?.Overlay.Enter())
            return MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                   == MessageBoxResult.Yes;
    }
}
