using FluentAssertions;
using VpnControl.Core.Servers;
using VpnControl.Core.Tests.Fakes;
using Xunit;

namespace VpnControl.Core.Tests.Servers;

public sealed class ServerCatalogTests
{
    private static ServerCatalog Catalog() => new(
        [
            TestServers.Create("lt-vln-01", city: "Vilnius", country: "LT"),
            TestServers.Create("lt-kun-01", city: "Kaunas", country: "LT"),
            TestServers.Create("de-fra-01", city: "Frankfurt", country: "DE"),
            TestServers.Create("de-ber-01", city: "Berlin", country: "DE", isEnabled: false),
        ],
        DateTimeOffset.UnixEpoch);

    [Fact]
    public void Gateways_are_ordered_by_country_then_city_then_identifier()
    {
        Catalog().Servers.Select(s => s.Id).Should().Equal("de-ber-01", "de-fra-01", "lt-kun-01", "lt-vln-01");
    }

    [Fact]
    public void Two_gateways_with_the_same_identifier_are_rejected()
    {
        // A duplicate would make Find ambiguous and would let selection rank the same gateway
        // twice, so it is caught at construction rather than tolerated.
        Action act = () => _ = new ServerCatalog(
            [TestServers.Create("dup"), TestServers.Create("dup")],
            DateTimeOffset.UnixEpoch);

        act.Should().Throw<ArgumentException>().WithMessage("*dup*");
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_an_unknown_identifier()
    {
        ServerCatalog catalog = Catalog();

        catalog.Find("LT-VLN-01")!.Id.Should().Be("lt-vln-01");
        catalog.Find("nope").Should().BeNull();
    }

    [Fact]
    public void Enabled_leaves_out_what_the_operator_has_withdrawn()
    {
        Catalog().Enabled().Select(s => s.Id).Should().NotContain("de-ber-01").And.HaveCount(3);
    }

    [Fact]
    public void InCountry_matches_without_regard_to_case()
    {
        Catalog().InCountry("lt").Select(s => s.Id).Should().BeEquivalentTo(["lt-vln-01", "lt-kun-01"]);
    }

    [Fact]
    public void Countries_lists_each_country_once_in_order()
    {
        Catalog().Countries().Should().Equal("DE", "LT");
    }

    [Fact]
    public void The_empty_catalog_is_usable_as_a_starting_state()
    {
        ServerCatalog.Empty.Count.Should().Be(0);
        ServerCatalog.Empty.Servers.Should().BeEmpty();
        ServerCatalog.Empty.Find("anything").Should().BeNull();
    }

    [Fact]
    public void The_demo_catalog_has_unique_identifiers_and_valid_keys()
    {
        // The demo data is used to seed the API and to run the desktop app, so a mistake in it
        // would look like a bug in either. Checking it here keeps that cheap.
        IReadOnlyList<VpnServer> servers = DemoCatalog.CreateServers();

        servers.Select(s => s.Id).Should().OnlyHaveUniqueItems();
        servers.Should().OnlyContain(s => Core.Crypto.WireGuardKeyPair.IsValidKey(s.PublicKey));
        servers.Should().OnlyContain(s => s.EndpointHost.EndsWith(".invalid", StringComparison.Ordinal));
        servers.Should().OnlyContain(s => s.LoadPercent >= 0 && s.LoadPercent <= 100);
    }
}
