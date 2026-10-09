using System.Diagnostics;

namespace ContainerForest.Cli.Login;

/// <summary>Opens a page for the person signing in.</summary>
public interface IBrowser
{
    /// <summary>False when no browser could be started, e.g. over SSH; the CLI then prints the address.</summary>
    bool TryOpen(Uri page);
}

/// <summary>The system's default browser: xdg-open, open or the Windows shell, through shell execute.</summary>
internal sealed class SystemBrowser : IBrowser
{
    public bool TryOpen(Uri page)
    {
        if (page.Scheme is not ("https" or "http"))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
            return process is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
