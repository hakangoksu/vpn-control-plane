using FluentAssertions;
using Microsoft.Extensions.Logging;
using VpnControl.Desktop.Logging;
using VpnControl.Desktop.Threading;
using VpnControl.Desktop.ViewModels;

namespace VpnControl.Desktop.Tests;

/// <summary>Covers the log pane and the provider that feeds it.</summary>
public sealed class LogViewModelTests
{
    [Fact]
    public void A_line_arrives_with_its_level_and_a_shortened_category()
    {
        var log = new LogViewModel(new ImmediateUiDispatcher());

        log.Write(LogLevel.Information, "VpnControl.Core.Connection.VpnConnectionManager", "Tunnel up.", null);

        LogEntry entry = log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Category.Should().Be("VpnConnectionManager");
        entry.Message.Should().Be("Tunnel up.");
        entry.LevelText.Should().Be("INFO");
        log.Latest.Should().BeSameAs(entry);
    }

    [Fact]
    public void An_exception_is_appended_to_the_line_rather_than_dropped()
    {
        var log = new LogViewModel(new ImmediateUiDispatcher());

        log.Write(LogLevel.Error, "Test", "Could not connect.", new InvalidOperationException("no route"));

        log.Entries.Single().Message.Should().Be("Could not connect. (InvalidOperationException: no route)");
    }

    [Fact]
    public void The_pane_is_capped_and_drops_the_oldest_lines()
    {
        var log = new LogViewModel(new ImmediateUiDispatcher());

        for (int i = 0; i < LogViewModel.MaxEntries + 25; i++)
        {
            log.Write(LogLevel.Information, "Test", $"line {i}", null);
        }

        log.Entries.Should().HaveCount(LogViewModel.MaxEntries);
        log.Entries[0].Message.Should().Be("line 25");
        log.Entries[^1].Message.Should().Be($"line {LogViewModel.MaxEntries + 24}");
    }

    [Fact]
    public void Clearing_empties_the_pane()
    {
        var log = new LogViewModel(new ImmediateUiDispatcher());
        log.Write(LogLevel.Warning, "Test", "something", null);

        log.ClearCommand.Execute(null);

        log.Entries.Should().BeEmpty();
        log.Latest.Should().BeNull();
    }

    [Fact]
    public void A_logger_built_on_the_provider_reaches_the_pane()
    {
        var log = new LogViewModel(new ImmediateUiDispatcher());
        using var provider = new SinkLoggerProvider(log);
        ILogger logger = provider.CreateLogger("VpnControl.Desktop.Tests.Example");

        logger.LogWarning("Load is {Load}%.", 96);

        LogEntry entry = log.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Be("Load is 96%.");
        entry.Category.Should().Be("Example");
        entry.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void Debug_lines_are_dropped_so_the_pane_is_not_drowned_by_framework_noise()
    {
        var log = new LogViewModel(new ImmediateUiDispatcher());
        using var provider = new SinkLoggerProvider(log);
        ILogger logger = provider.CreateLogger("Avalonia.Something");

        logger.LogDebug("a frame was rendered");

        log.Entries.Should().BeEmpty();
    }
}
