namespace VpnControl.Desktop.Threading;

/// <summary>
/// Runs posted work on the calling thread.
/// </summary>
/// <remarks>
/// For tests and for the XAML previewer, neither of which has an Avalonia UI thread to
/// post to. Using it in the running application would defeat the marshalling it stands
/// in for, so nothing outside those two cases constructs it.
/// </remarks>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public bool IsOnUiThread => true;

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
