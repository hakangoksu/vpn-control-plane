using FluentAssertions;
using VpnControl.Core.Crypto;
using Xunit;

namespace VpnControl.Core.Tests.Crypto;

public sealed class WireGuardKeyPairTests
{
    [Fact]
    public void A_generated_pair_has_two_distinct_thirty_two_byte_keys()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        Convert.FromBase64String(keys.PublicKeyBase64).Should().HaveCount(WireGuardKeyPair.KeyLength);
        Convert.FromBase64String(keys.PrivateKeyBase64).Should().HaveCount(WireGuardKeyPair.KeyLength);
        keys.PublicKeyBase64.Should().NotBe(keys.PrivateKeyBase64);
    }

    [Fact]
    public void Two_generated_pairs_differ()
    {
        using WireGuardKeyPair first = WireGuardKeyPair.Generate();
        using WireGuardKeyPair second = WireGuardKeyPair.Generate();

        second.PrivateKeyBase64.Should().NotBe(first.PrivateKeyBase64);
        second.PublicKeyBase64.Should().NotBe(first.PublicKeyBase64);
    }

    [Fact]
    public void A_private_key_derives_the_same_public_key_every_time()
    {
        using WireGuardKeyPair original = WireGuardKeyPair.Generate();
        string privateKey = original.PrivateKeyBase64;

        using WireGuardKeyPair restored = WireGuardKeyPair.FromPrivateKey(privateKey);

        restored.PublicKeyBase64.Should().Be(original.PublicKeyBase64);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all !!")]
    [InlineData("c2hvcnQ=")]
    public void FromPrivateKey_rejects_anything_that_is_not_a_thirty_two_byte_key(string candidate)
    {
        Action act = () => WireGuardKeyPair.FromPrivateKey(candidate);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsValidKey_accepts_a_real_key_and_rejects_the_near_misses()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        WireGuardKeyPair.IsValidKey(keys.PublicKeyBase64).Should().BeTrue();

        WireGuardKeyPair.IsValidKey(null).Should().BeFalse();
        WireGuardKeyPair.IsValidKey("").Should().BeFalse();
        WireGuardKeyPair.IsValidKey("????").Should().BeFalse();

        // Valid base64, wrong length. This is the case a naive base64 check would pass, and
        // the one that would produce a configuration WireGuard silently refuses.
        WireGuardKeyPair.IsValidKey(Convert.ToBase64String(new byte[16])).Should().BeFalse();
        WireGuardKeyPair.IsValidKey(Convert.ToBase64String(new byte[64])).Should().BeFalse();
    }

    [Fact]
    public void Reading_the_private_key_after_disposal_throws()
    {
        WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        keys.Dispose();

        Action act = () => _ = keys.PrivateKeyBase64;

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void The_public_key_is_still_readable_after_disposal()
    {
        // Only the private half is secret, and the public half is often still needed while
        // cleaning up a session, for example to release a registration.
        WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        string publicKey = keys.PublicKeyBase64;

        keys.Dispose();

        keys.PublicKeyBase64.Should().Be(publicKey);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        keys.Dispose();
        Action act = keys.Dispose;

        act.Should().NotThrow();
    }
}
