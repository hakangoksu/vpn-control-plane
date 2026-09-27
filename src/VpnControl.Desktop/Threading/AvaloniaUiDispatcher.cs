using Avalonia.Threading;

namespace VpnControl.Desktop.Threading;

/// <summary>
/// The real dispatcher, backed by Avalonia's UI thread.
/// </summary>
/// <remarks>
/// The whole of the Avalonia threading dependency is contained in this one class, which
/// is the point of the interface it implements.
/// </remarks>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // Running inline when the caller is already on the UI thread keeps an update
        // that originated in a command from being deferred to the next dispatcher pass,
        // where the user would see the old value for one frame.
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }
}
