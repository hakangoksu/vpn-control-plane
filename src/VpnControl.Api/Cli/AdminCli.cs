using System.Globalization;
using VpnControl.Api.Data;
using VpnControl.Api.Security;

namespace VpnControl.Api.Cli;

/// <summary>
/// Parses <c>admin ...</c> arguments and runs the matching <see cref="AdminCommands"/> call.
/// </summary>
/// <remarks>
/// Hand written rather than built on a parsing library. There are six commands with a few
/// named options each, and a dependency for that would be larger than the parser.
/// <para>
/// Secrets travel on standard input and standard output only. An agent token passed as an
/// argument would be visible to every user on the host in the process list and would be
/// kept in shell history.
/// </para>
/// </remarks>
public static class AdminCli
{
    /// <summary>Usage text printed for <c>admin help</c> or an unrecognised command.</summary>
    public const string Usage = """
        Usage: VpnControl.Api admin <command>

          device add <name>          Enroll a device. Prints its token once.
          device list                List devices.
          device revoke <id>         Revoke a device and drop its peers.
          gateway upsert             Create or update a gateway. Reads the agent token from stdin.
              --id <id> --name <name> --city <city> --country <CC>
              --host <host> --port <port> --public-key <key>
              [--ipv6-egress true|false] [--enabled true|false]
          gateway list               List gateways.
          gateway remove <id>        Delete a gateway and its peers.
          token gateway              Print a new agent token without storing anything.
        """;

    /// <summary>Whether the arguments ask for the admin command line rather than the web host.</summary>
    /// <param name="args">Process arguments.</param>
    /// <returns><c>true</c> when the first argument is <c>admin</c>.</returns>
    public static bool IsAdminInvocation(string[] args) =>
        args is { Length: > 0 } && string.Equals(args[0], "admin", StringComparison.Ordinal);

    /// <summary>Runs one admin command.</summary>
    /// <param name="args">Arguments after <c>admin</c>.</param>
    /// <param name="commands">Actions to run.</param>
    /// <param name="input">Where the agent token is read from.</param>
    /// <param name="output">Where results are written.</param>
    /// <param name="error">Where problems are written.</param>
    /// <param name="cancellationToken">Abandons the work.</param>
    /// <returns>Process exit code: 0 on success, 1 on a failed command, 2 on a usage error.</returns>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        AdminCommands commands,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        string area = args.Count > 0 ? args[0] : "help";
        string action = args.Count > 1 ? args[1] : string.Empty;

        try
        {
            switch (area, action)
            {
                case ("device", "add") when args.Count == 3:
                    {
                        (DeviceRecord device, string token) = await commands.AddDeviceAsync(args[2], cancellationToken).ConfigureAwait(false);
                        await error.WriteLineAsync($"Enrolled device {device.Id} ({device.Name}). The token is shown once:").ConfigureAwait(false);
                        await output.WriteLineAsync(token).ConfigureAwait(false);
                        return 0;
                    }

                case ("device", "list"):
                    foreach ((DeviceRecord device, int peers) in await commands.ListDevicesAsync(cancellationToken).ConfigureAwait(false))
                    {
                        string state = device.RevokedAt is { } revoked
                            ? $"revoked {revoked:u}"
                            : "active";
                        await output.WriteLineAsync(
                            $"{device.Id}  {device.Name,-20}  {state,-28}  peers={peers}  created {device.CreatedAt:u}").ConfigureAwait(false);
                    }

                    return 0;

                case ("device", "revoke") when args.Count == 3:
                    {
                        int? removed = await commands.RevokeDeviceAsync(args[2], cancellationToken).ConfigureAwait(false);
                        if (removed is null)
                        {
                            await error.WriteLineAsync($"No device with id '{args[2]}'.").ConfigureAwait(false);
                            return 1;
                        }

                        await output.WriteLineAsync($"Revoked {args[2]}, removed {removed} peer registration(s).").ConfigureAwait(false);
                        return 0;
                    }

                case ("gateway", "upsert"):
                    {
                        Dictionary<string, string> options = ParseOptions(args.Skip(2).ToList());
                        string? token = (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();

                        var gateway = new ServerRecord
                        {
                            Id = Required(options, "id"),
                            Name = Required(options, "name"),
                            City = Required(options, "city"),
                            Country = Required(options, "country"),
                            EndpointHost = Required(options, "host"),
                            EndpointPort = int.Parse(Required(options, "port"), NumberStyles.None, CultureInfo.InvariantCulture),
                            PublicKey = Required(options, "public-key"),
                            Ipv6Egress = Flag(options, "ipv6-egress", defaultValue: false),
                            IsEnabled = Flag(options, "enabled", defaultValue: true),
                        };

                        bool created = await commands.UpsertGatewayAsync(gateway, token ?? string.Empty, cancellationToken).ConfigureAwait(false);
                        await output.WriteLineAsync($"{(created ? "Created" : "Updated")} gateway {gateway.Id}.").ConfigureAwait(false);
                        return 0;
                    }

                case ("gateway", "list"):
                    foreach (ServerRecord gateway in await commands.ListGatewaysAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await output.WriteLineAsync(
                            $"{gateway.Id,-10}  {gateway.City}, {gateway.Country}  {gateway.EndpointHost}:{gateway.EndpointPort}  ipv6={gateway.Ipv6Egress}  enabled={gateway.IsEnabled}  agent={(gateway.AgentTokenHash is null ? "no" : "yes")}").ConfigureAwait(false);
                    }

                    return 0;

                case ("gateway", "remove") when args.Count == 3:
                    if (await commands.RemoveGatewayAsync(args[2], cancellationToken).ConfigureAwait(false))
                    {
                        await output.WriteLineAsync($"Removed gateway {args[2]}.").ConfigureAwait(false);
                        return 0;
                    }

                    await error.WriteLineAsync($"No gateway with id '{args[2]}'.").ConfigureAwait(false);
                    return 1;

                case ("token", "gateway"):
                    await output.WriteLineAsync(AccessTokens.Create(AccessTokens.GatewayPrefix)).ConfigureAwait(false);
                    return 0;

                default:
                    await error.WriteLineAsync(Usage).ConfigureAwait(false);
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            // Input mistakes are reported as one line. Anything else, a database that
            // cannot be opened for instance, propagates with its stack trace, because the
            // operator needs the detail.
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }
    }

    private static Dictionary<string, string> ParseOptions(List<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < args.Count; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count)
            {
                throw new ArgumentException($"Expected '--name value' pairs, found '{args[i]}'.");
            }

            options[args[i][2..]] = args[i + 1];
        }

        return options;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{name} is required.");

    private static bool Flag(Dictionary<string, string> options, string name, bool defaultValue) =>
        !options.TryGetValue(name, out string? value)
            ? defaultValue
            : bool.TryParse(value, out bool parsed)
                ? parsed
                : throw new ArgumentException($"--{name} must be true or false.");
}
