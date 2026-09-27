using FluentAssertions;
using VpnControl.Core.Connection;
using Xunit;

namespace VpnControl.Core.Tests.Connection;

/// <summary>
/// Covers every pair of states, not only the interesting ones. The table is the
/// specification, so a change to it that nobody meant should break a test.
/// </summary>
public sealed class ConnectionStateMachineTests
{
    private static readonly ConnectionState[] AllStates = Enum.GetValues<ConnectionState>();

    /// <summary>Every ordered pair of states, for the exhaustive transition theories.</summary>
    public static TheoryData<ConnectionState, ConnectionState> AllStatePairs()
    {
        var data = new TheoryData<ConnectionState, ConnectionState>();
        foreach (ConnectionState from in AllStates)
        {
            foreach (ConnectionState to in AllStates)
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllStatePairs))]
    public void TransitionTo_matches_the_published_table(ConnectionState from, ConnectionState to)
    {
        var machine = new ConnectionStateMachine(from);
        bool expected = ConnectionStateMachine.TransitionTable.TryGetValue(from, out var allowed) && allowed.Contains(to);

        bool actual = machine.TryTransitionTo(to);

        actual.Should().Be(expected);
        machine.Current.Should().Be(expected ? to : from);
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connecting)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Connected)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Faulted)]
    [InlineData(ConnectionState.Connected, ConnectionState.Switching)]
    [InlineData(ConnectionState.Connected, ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Connected, ConnectionState.Faulted)]
    [InlineData(ConnectionState.Switching, ConnectionState.Connected)]
    [InlineData(ConnectionState.Switching, ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Switching, ConnectionState.Faulted)]
    [InlineData(ConnectionState.Disconnecting, ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Disconnecting, ConnectionState.Faulted)]
    [InlineData(ConnectionState.Faulted, ConnectionState.Connecting)]
    [InlineData(ConnectionState.Faulted, ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Faulted, ConnectionState.Disconnected)]
    public void Legal_transitions_are_accepted(ConnectionState from, ConnectionState to)
    {
        var machine = new ConnectionStateMachine(from);

        machine.TransitionTo(to);

        machine.Current.Should().Be(to);
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connected)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Switching)]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Switching)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Connecting)]
    [InlineData(ConnectionState.Connected, ConnectionState.Connecting)]
    [InlineData(ConnectionState.Connected, ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Disconnecting, ConnectionState.Connected)]
    [InlineData(ConnectionState.Disconnecting, ConnectionState.Connecting)]
    [InlineData(ConnectionState.Faulted, ConnectionState.Connected)]
    [InlineData(ConnectionState.Faulted, ConnectionState.Switching)]
    public void Illegal_transitions_throw_and_leave_the_state_alone(ConnectionState from, ConnectionState to)
    {
        var machine = new ConnectionStateMachine(from);

        Action act = () => machine.TransitionTo(to);

        act.Should().Throw<InvalidStateTransitionException>()
            .Which.Should().Match<InvalidStateTransitionException>(ex => ex.From == from && ex.To == to);
        machine.Current.Should().Be(from);
    }

    [Fact]
    public void A_transition_raises_the_event_with_both_states_and_the_reason()
    {
        var machine = new ConnectionStateMachine();
        ConnectionStateChangedEventArgs? observed = null;
        machine.StateChanged += (_, args) => observed = args;

        machine.TransitionTo(ConnectionState.Connecting, "user clicked connect");

        observed.Should().NotBeNull();
        observed!.Previous.Should().Be(ConnectionState.Disconnected);
        observed.Current.Should().Be(ConnectionState.Connecting);
        observed.Reason.Should().Be("user clicked connect");
    }

    [Fact]
    public void A_rejected_transition_raises_no_event()
    {
        var machine = new ConnectionStateMachine();
        int raised = 0;
        machine.StateChanged += (_, _) => raised++;

        machine.TryTransitionTo(ConnectionState.Connected).Should().BeFalse();

        raised.Should().Be(0);
    }

    [Fact]
    public void IsBusy_is_true_only_while_an_operation_is_in_flight()
    {
        new ConnectionStateMachine(ConnectionState.Connecting).IsBusy.Should().BeTrue();
        new ConnectionStateMachine(ConnectionState.Switching).IsBusy.Should().BeTrue();
        new ConnectionStateMachine(ConnectionState.Disconnecting).IsBusy.Should().BeTrue();

        new ConnectionStateMachine(ConnectionState.Disconnected).IsBusy.Should().BeFalse();
        new ConnectionStateMachine(ConnectionState.Connected).IsBusy.Should().BeFalse();
        new ConnectionStateMachine(ConnectionState.Faulted).IsBusy.Should().BeFalse();
    }

    [Fact]
    public void Every_state_has_a_row_in_the_table()
    {
        // A state with no row can never be left, which would strand a session. Adding a
        // state to the enum without a row is the mistake this catches.
        ConnectionStateMachine.TransitionTable.Keys.Should().BeEquivalentTo(AllStates);
    }

    [Fact]
    public void Connected_is_reachable_and_leavable_from_the_starting_state()
    {
        var machine = new ConnectionStateMachine();

        machine.TransitionTo(ConnectionState.Connecting);
        machine.TransitionTo(ConnectionState.Connected);
        machine.IsConnected.Should().BeTrue();

        machine.TransitionTo(ConnectionState.Disconnecting);
        machine.TransitionTo(ConnectionState.Disconnected);

        machine.Current.Should().Be(ConnectionState.Disconnected);
    }
}
