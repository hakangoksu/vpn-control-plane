using Microsoft.EntityFrameworkCore;

namespace VpnControl.Api.Data;

/// <summary>
/// The control plane's database: gateways and the peers registered on them.
/// </summary>
/// <remarks>
/// SQLite, because a single file needs no service to be installed before the project can be
/// run, and because nothing here needs more. The provider is chosen in one place at startup,
/// so moving to PostgreSQL is a package and a connection string rather than a rewrite.
/// </remarks>
/// <param name="options">Provider and connection configured by the host.</param>
public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : DbContext(options)
{
    /// <summary>Gateways the control plane knows about.</summary>
    public DbSet<ServerRecord> Servers => Set<ServerRecord>();

    /// <summary>Peer registrations currently held.</summary>
    public DbSet<PeerRecord> Peers => Set<PeerRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PeerRecord>(peer =>
        {
            // Deleting a gateway takes its registrations with it. They describe an address
            // on that gateway and mean nothing without it.
            peer.HasOne(p => p.Server)
                .WithMany(s => s.Peers)
                .HasForeignKey(p => p.ServerId)
                .OnDelete(DeleteBehavior.Cascade);

            // The same key must not be admitted twice to one gateway: it would be handed a
            // second address that the first tunnel would never use. Enforced in the database
            // rather than only in the handler, because two concurrent requests can both pass
            // an application-level check before either writes.
            peer.HasIndex(p => new { p.ServerId, p.PublicKey }).IsUnique();

            // Same reasoning for the address itself.
            peer.HasIndex(p => new { p.ServerId, p.AddressIndex }).IsUnique();
        });

        modelBuilder.Entity<ServerRecord>()
            .HasIndex(s => new { s.Country, s.City });
    }
}
