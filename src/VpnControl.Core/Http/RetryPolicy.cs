namespace VpnControl.Core.Http;

/// <summary>
/// Retries an operation a bounded number of times with exponential backoff.
/// </summary>
/// <remarks>
/// A real service would take this from Polly. It is written out here because the
/// point of this project is to show the mechanics: how many attempts, how long
/// between them, which failures are worth repeating, and how cancellation cuts the
/// wait short rather than being noticed only after it.
/// <para>
/// The delay function is injected so tests can run the policy without waiting.
/// That is the same dependency inversion used elsewhere in this library, applied
/// to the clock instead of to a service.
/// </para>
/// </remarks>
public sealed class RetryPolicy
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Creates a policy.</summary>
    /// <param name="maxAttempts">
    /// Total attempts including the first one. A value of 1 means no retries.
    /// </param>
    /// <param name="baseDelay">Wait before the second attempt.</param>
    /// <param name="maxDelay">Ceiling the doubling backoff is clamped to.</param>
    /// <param name="delay">
    /// How to wait. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>;
    /// tests pass a function that records the requested delay and returns at once.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A non-positive attempt count or a negative delay was given.
    /// </exception>
    public RetryPolicy(
        int maxAttempts = 3,
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "At least one attempt is required.");
        }

        BaseDelay = baseDelay ?? TimeSpan.FromMilliseconds(200);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(5);

        if (BaseDelay < TimeSpan.Zero || MaxDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay), "Delays cannot be negative.");
        }

        MaxAttempts = maxAttempts;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Total attempts this policy will make.</summary>
    public int MaxAttempts { get; }

    /// <summary>Wait before the second attempt, doubled for each attempt after it.</summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>Upper bound on any single wait.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying while
    /// <paramref name="shouldRetry"/> accepts the thrown exception and attempts
    /// remain.
    /// </summary>
    /// <typeparam name="T">Result the operation produces.</typeparam>
    /// <param name="operation">
    /// The work to attempt. It receives the one-based attempt number, which is
    /// useful for logging without the caller having to count.
    /// </param>
    /// <param name="shouldRetry">
    /// Decides whether a given failure is worth another attempt. A timeout or a
    /// 503 usually is; a 400 never is.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation and any pending wait.</param>
    /// <returns>The result of the first successful attempt.</returns>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled. Cancellation is never retried: the caller asked to
    /// stop, so swallowing it and trying again would ignore that request.
    /// </exception>
    public async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> operation,
        Func<Exception, bool> shouldRetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(shouldRetry);

        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts && shouldRetry(ex))
            {
                // The exception is deliberately not logged here. This type does not
                // know what the operation was, so the caller is in a far better
                // position to describe the failure.
                await _delay(DelayForAttempt(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Wait to observe after the given one-based attempt has failed.
    /// </summary>
    /// <param name="attempt">The attempt that just failed, counting from 1.</param>
    /// <returns>The backoff duration, clamped to <see cref="MaxDelay"/>.</returns>
    /// <remarks>
    /// Doubling per attempt keeps a struggling service from being hammered. A
    /// production policy would also add random jitter so that a fleet of clients
    /// retrying after the same outage does not arrive in one synchronised wave;
    /// it is left out here so the delays stay predictable in tests.
    /// </remarks>
    public TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "Attempts are counted from 1.");
        }

        // Ticks are multiplied rather than milliseconds so the doubling keeps
        // sub-millisecond precision, and the shift is bounded to avoid overflow.
        int doublings = Math.Min(attempt - 1, 16);
        long ticks = BaseDelay.Ticks * (1L << doublings);
        return ticks >= MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks(ticks);
    }
}
