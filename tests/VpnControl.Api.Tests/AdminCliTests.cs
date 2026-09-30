using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VpnControl.Api.Cli;
using VpnControl.Api.Data;
using VpnControl.Api.Security;
using VpnControl.Core.Crypto;
using Xunit;

namespace VpnControl.Api.Tests;

/// <summary>
/// Covers the operator's command line: what it prints, what it stores, and what it refuses.
/// </summary>
public sealed class AdminCliTests(ControlPlaneApiFactory factory) : IClassFixture<ControlPlaneApiFactory>
{
    private readonly ControlPlaneApiFactory _factory = factory;

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string stdin, params string[] args)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AdminCommands commands = scope.ServiceProvider.GetRequiredService<AdminCommands>();

        using var input = new StringReader(stdin);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await AdminCli.RunAsync(args, commands, input, output, error, CancellationToken.None);
        return (exitCode, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Adding_a_device_prints_a_token_once_and_stores_only_its_hash()
    {
        (int exitCode, string output, _) = await RunAsync(string.Empty, "device", "add", "cli-laptop");

        exitCode.Should().Be(0);
        string token = output.Trim();
        AccessTokens.HasShape(token, AccessTokens.DevicePrefix).Should().BeTrue();

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ControlPlaneDbContext db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        DeviceRecord stored = await db.Devices.SingleAsync(d => d.Name == "cli-laptop");

        stored.TokenHash.Should().Be(AccessTokens.Hash(token));
        stored.TokenHash.Should().NotContain(token);
    }

    [Fact]
    public async Task Gateway_upsert_reads_the_agent_token_from_standard_input()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        string token = AccessTokens.Create(AccessTokens.GatewayPrefix);

        (int exitCode, string output, string error) = await RunAsync(
            token + "\n",
            "gateway", "upsert",
            "--id", "cli-gw", "--name", "CLI 1", "--city", "Riga", "--country", "LV",
            "--host", "gw.example.invalid", "--port", "51820", "--public-key", keys.PublicKeyBase64,
            "--ipv6-egress", "true");

        exitCode.Should().Be(0, error);
        output.Should().Contain("Created gateway cli-gw");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ServerRecord stored = await scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>()
            .Servers.SingleAsync(s => s.Id == "cli-gw");

        stored.AgentTokenHash.Should().Be(AccessTokens.Hash(token));
        stored.Ipv6Egress.Should().BeTrue();
    }

    [Fact]
    public async Task Gateway_upsert_without_a_valid_agent_token_is_refused()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        (int exitCode, _, string error) = await RunAsync(
            "not-a-token\n",
            "gateway", "upsert",
            "--id", "cli-gw-bad", "--name", "Bad", "--city", "Riga", "--country", "LV",
            "--host", "gw.example.invalid", "--port", "51820", "--public-key", keys.PublicKeyBase64);

        exitCode.Should().Be(2);
        error.Should().Contain("agent token");
    }

    [Theory]
    [InlineData("--country", "lv")]
    [InlineData("--port", "70000")]
    [InlineData("--public-key", "c2hvcnQ=")]
    [InlineData("--host", "not a host")]
    public async Task Gateway_upsert_rejects_malformed_fields(string option, string value)
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        var args = new Dictionary<string, string>
        {
            ["--id"] = "cli-gw-malformed",
            ["--name"] = "Malformed",
            ["--city"] = "Riga",
            ["--country"] = "LV",
            ["--host"] = "gw.example.invalid",
            ["--port"] = "51820",
            ["--public-key"] = keys.PublicKeyBase64,
        };
        args[option] = value;

        (int exitCode, _, _) = await RunAsync(
            AccessTokens.Create(AccessTokens.GatewayPrefix) + "\n",
            ["gateway", "upsert", .. args.SelectMany(a => new[] { a.Key, a.Value })]);

        exitCode.Should().NotBe(0);
    }

    [Fact]
    public async Task Revoking_an_unknown_device_fails()
    {
        (int exitCode, _, string error) = await RunAsync(string.Empty, "device", "revoke", "no-such-device");

        exitCode.Should().Be(1);
        error.Should().Contain("No device");
    }

    [Fact]
    public async Task An_unknown_command_prints_usage()
    {
        (int exitCode, _, string error) = await RunAsync(string.Empty, "frobnicate");

        exitCode.Should().Be(2);
        error.Should().Contain("Usage:");
    }
}
