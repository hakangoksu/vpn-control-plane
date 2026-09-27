namespace VpnControl.Desktop.Threading;

/// <summary>
/// Moves work onto the thread that owns the user interface.
/// </summary>
/// <remarks>
/// The view models need this because almost nothing calls them back on the UI thread.
/// A tunnel backend raises its state change on whichever thread brought the interface
/// up, the connection manager forwards it unchanged, and a logger writes from wherever
/// the log statement was reached. Touching an observable collection from any of those
/// threads is how a desktop application gets an intermittent crash on a background
/// update.
/// <para>
/// It is an interface rather than a direct call to Avalonia's dispatcher so the view
/// models stay free of UI framework types: that is what lets them be constructed and
/// driven in a unit test with no window and no Avalonia runtime.
/// </para>
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>Whether the calling thread is already the UI thread.</summary>
    bool IsOnUiThread { get; }

    /// <summary>Queues work on the UI thread and returns without waiting for it.</summary>
    /// <param name="action">The work to run.</param>
    /// <remarks>
    /// Posting rather than invoking is deliberate. A blocking call from a background
    /// thread into the UI thread, while the UI thread is waiting on that same operation,
    /// is the classic desktop deadlock.
    /// </remarks>
    void Post(Action action);
}
