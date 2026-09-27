using FluentAssertions;
using VpnControl.Core.Http;
using Xunit;

namespace VpnControl.Core.Tests.Http;

public sealed class RetryPolicyTests
{
    /// <summary>
    /// Records the delays asked for and returns immediately, so the tests assert on the
    /// backoff without spending the time.
    /// </summary>
    private sealed class DelayRecorder
    {
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_successful_operation_runs_once()
    {
        var recorder = new DelayRecorder();
        var policy = new RetryPolicy(maxAttempts: 3, delay: recorder.DelayAsync);
        int calls = 0;

        int result = await policy.ExecuteAsync(
            (_, _) => { calls++; return Task.FromResult(42); },
            static _ => true);

        result.Should().Be(42);
        calls.Should().Be(1);
        recorder.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task A_transient_failure_is_retried_until_it_succeeds()
    {
        var recorder = new DelayRecorder();
        var policy = new RetryPolicy(maxAttempts: 4, baseDelay: TimeSpan.FromMilliseconds(10), delay: recorder.DelayAsync);

        string result = await policy.ExecuteAsync(
            (attempt, _) => attempt < 3
                ? throw new TimeoutException("still warming up")
                : Task.FromResult($"ok on attempt {attempt}"),
            static ex => ex is TimeoutException);

        result.Should().Be("ok on attempt 3");
        recorder.Delays.Should().Equal(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public async Task The_last_failure_is_thrown_once_the_attempts_run_out()
    {
        var recorder = new DelayRecorder();
        var policy = new RetryPolicy(maxAttempts: 3, delay: recorder.DelayAsync);
        int calls = 0;

        Func<Task> act = () => policy.ExecuteAsync<int>(
            (attempt, _) => { calls++; throw new TimeoutException($"attempt {attempt}"); },
            static _ => true);

        (await act.Should().ThrowAsync<TimeoutException>()).WithMessage("attempt 3");
        calls.Should().Be(3);
        recorder.Delays.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failure_the_predicate_rejects_is_not_retried()
    {
        var recorder = new DelayRecorder();
        var policy = new RetryPolicy(maxAttempts: 5, delay: recorder.DelayAsync);
        int calls = 0;

        Func<Task> act = () => policy.ExecuteAsync<int>(
            (_, _) => { calls++; throw new InvalidOperationException("bad request"); },
            static ex => ex is TimeoutException);

        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(1);
        recorder.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task One_attempt_means_no_retries()
    {
        var policy = new RetryPolicy(maxAttempts: 1);
        int calls = 0;

        Func<Task> act = () => policy.ExecuteAsync<int>(
            (_, _) => { calls++; throw new TimeoutException(); },
            static _ => true);

        await act.Should().ThrowAsync<TimeoutException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_is_never_retried()
    {
        var recorder = new DelayRecorder();
        var policy = new RetryPolicy(maxAttempts: 5, delay: recorder.DelayAsync);
        using var cts = new CancellationTokenSource();
        int calls = 0;

        Func<Task> act = () => policy.ExecuteAsync<int>(
            (_, token) =>
            {
                calls++;
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            },
            static _ => true,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1);
        recorder.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_already_cancelled_means_the_operation_never_runs()
    {
        var policy = new RetryPolicy();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        int calls = 0;

        Func<Task> act = () => policy.ExecuteAsync(
            (_, _) => { calls++; return Task.FromResult(0); },
            static _ => true,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(0);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(2, 200)]
    [InlineData(3, 400)]
    [InlineData(4, 800)]
    [InlineData(5, 1000)]
    [InlineData(20, 1000)]
    public void The_backoff_doubles_and_then_stops_at_the_ceiling(int attempt, int expectedMilliseconds)
    {
        var policy = new RetryPolicy(
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: TimeSpan.FromMilliseconds(1000));

        policy.DelayForAttempt(attempt).Should().Be(TimeSpan.FromMilliseconds(expectedMilliseconds));
    }

    [Fact]
    public void A_non_positive_attempt_count_is_rejected_at_construction()
    {
        Action act = () => _ = new RetryPolicy(maxAttempts: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Asking_for_the_delay_of_a_zeroth_attempt_is_rejected()
    {
        Action act = () => new RetryPolicy().DelayForAttempt(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
