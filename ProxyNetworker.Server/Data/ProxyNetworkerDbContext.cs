using Microsoft.EntityFrameworkCore;

namespace ProxyNetworker.Server.Data;

public sealed class ProxyNetworkerDbContext : DbContext
{
    public ProxyNetworkerDbContext(DbContextOptions<ProxyNetworkerDbContext> options)
        : base(options)
    {
    }

    public DbSet<UserAccount> Users => Set<UserAccount>();

    public DbSet<VirtualNetworkRecord> VirtualNetworks => Set<VirtualNetworkRecord>();

    public DbSet<PortTunnelDefinitionRecord> PortTunnelDefinitions => Set<PortTunnelDefinitionRecord>();

    public DbSet<AccessTokenRecord> AccessTokens => Set<AccessTokenRecord>();

    public DbSet<SystemSettingsRecord> SystemSettings => Set<SystemSettingsRecord>();

    public DbSet<VirtualNetworkSessionRecord> VirtualNetworkSessions => Set<VirtualNetworkSessionRecord>();

    public DbSet<VirtualNetworkNodeBanRecord> VirtualNetworkNodeBans => Set<VirtualNetworkNodeBanRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<UserAccount>();
        user.HasKey(item => item.Id);
        user.Property(item => item.Id).HasMaxLength(32);
        user.Property(item => item.Username).HasMaxLength(64).IsRequired();
        user.Property(item => item.PasswordHash).HasMaxLength(512).IsRequired();
        user.Property(item => item.Role).HasMaxLength(32).IsRequired();
        user.HasIndex(item => item.Username).IsUnique();

        var virtualNetwork = modelBuilder.Entity<VirtualNetworkRecord>();
        virtualNetwork.HasKey(item => item.Id);
        virtualNetwork.Property(item => item.Id).HasMaxLength(32);
        virtualNetwork.Property(item => item.OwnerUserId).HasMaxLength(32).IsRequired();
        virtualNetwork.Property(item => item.Name).HasMaxLength(128).IsRequired();
        virtualNetwork.Property(item => item.GatewayAddress).HasMaxLength(64).IsRequired();
        virtualNetwork.Property(item => item.TransportProtocol).HasMaxLength(8).IsRequired();
        virtualNetwork.HasIndex(item => item.OwnerUserId);
        virtualNetwork.HasIndex(item => item.ListenPort);
        virtualNetwork.HasIndex(item => item.CreatedAtUnixMs);
        virtualNetwork.HasIndex(item => new { item.OwnerUserId, item.CreatedAtUnixMs });

        var portTunnel = modelBuilder.Entity<PortTunnelDefinitionRecord>();
        portTunnel.HasKey(item => item.Id);
        portTunnel.Property(item => item.Id).HasMaxLength(32);
        portTunnel.Property(item => item.OwnerUserId).HasMaxLength(32).IsRequired();
        portTunnel.Property(item => item.Name).HasMaxLength(128).IsRequired();
        portTunnel.Property(item => item.Protocol).HasMaxLength(16).IsRequired();
        portTunnel.Property(item => item.PrivateHost).HasMaxLength(256).IsRequired();
        portTunnel.HasIndex(item => item.OwnerUserId);
        portTunnel.HasIndex(item => item.PublicPort).IsUnique();
        portTunnel.HasIndex(item => item.CreatedAtUnixMs);
        portTunnel.HasIndex(item => new { item.OwnerUserId, item.CreatedAtUnixMs });

        var accessToken = modelBuilder.Entity<AccessTokenRecord>();
        accessToken.HasKey(item => item.Id);
        accessToken.Property(item => item.Id).HasMaxLength(32);
        accessToken.Property(item => item.ScopeKind).HasMaxLength(32).IsRequired();
        accessToken.Property(item => item.ResourceId).HasMaxLength(32).IsRequired();
        accessToken.Property(item => item.TokenHash).HasMaxLength(128).IsRequired();
        accessToken.Property(item => item.ProtectedTokenValue).HasMaxLength(2048);
        accessToken.Property(item => item.TokenPreview).HasMaxLength(24).IsRequired();
        accessToken.Property(item => item.TokenType).HasMaxLength(32).IsRequired();
        accessToken.Property(item => item.VirtualAddress).HasMaxLength(64);
        accessToken.Property(item => item.CreatedByUserId).HasMaxLength(32).IsRequired();
        accessToken.HasIndex(item => item.TokenHash).IsUnique();
        accessToken.HasIndex(item => new { item.ScopeKind, item.ResourceId });
        accessToken.HasIndex(item => new { item.ScopeKind, item.ResourceId, item.VirtualAddress }).IsUnique();
        accessToken.HasIndex(item => new { item.ScopeKind, item.ResourceId, item.CreatedAtUnixMs, item.Id });

        var systemSettings = modelBuilder.Entity<SystemSettingsRecord>();
        systemSettings.HasKey(item => item.Id);
        systemSettings.Property(item => item.Id).HasMaxLength(32);

        var virtualNetworkSession = modelBuilder.Entity<VirtualNetworkSessionRecord>();
        virtualNetworkSession.HasKey(item => item.Id);
        virtualNetworkSession.Property(item => item.Id).HasMaxLength(32);
        virtualNetworkSession.Property(item => item.NetworkId).HasMaxLength(32).IsRequired();
        virtualNetworkSession.Property(item => item.VirtualAddress).HasMaxLength(64).IsRequired();
        virtualNetworkSession.Property(item => item.TokenId).HasMaxLength(32).IsRequired();
        virtualNetworkSession.Property(item => item.TokenPreview).HasMaxLength(32).IsRequired();
        virtualNetworkSession.Property(item => item.NodeName).HasMaxLength(256).IsRequired();
        virtualNetworkSession.Property(item => item.MachineName).HasMaxLength(256).IsRequired();
        virtualNetworkSession.Property(item => item.OsDescription).HasMaxLength(512).IsRequired();
        virtualNetworkSession.Property(item => item.OsArchitecture).HasMaxLength(64).IsRequired();
        virtualNetworkSession.Property(item => item.ProcessArchitecture).HasMaxLength(64).IsRequired();
        virtualNetworkSession.Property(item => item.ClientVersion).HasMaxLength(64).IsRequired();
        virtualNetworkSession.Property(item => item.PublicEndpoint).HasMaxLength(256).IsRequired();
        virtualNetworkSession.Property(item => item.LocalEndpoints).HasMaxLength(2048).IsRequired();
        virtualNetworkSession.HasIndex(item => new { item.NetworkId, item.DisconnectedAtUnixMs });
        virtualNetworkSession.HasIndex(item => new { item.NetworkId, item.LastSeenAtUnixMs });
        virtualNetworkSession.HasIndex(item => new { item.NetworkId, item.VirtualAddress });

        var virtualNetworkBan = modelBuilder.Entity<VirtualNetworkNodeBanRecord>();
        virtualNetworkBan.HasKey(item => item.Id);
        virtualNetworkBan.Property(item => item.Id).HasMaxLength(32);
        virtualNetworkBan.Property(item => item.NetworkId).HasMaxLength(32).IsRequired();
        virtualNetworkBan.Property(item => item.VirtualAddress).HasMaxLength(64).IsRequired();
        virtualNetworkBan.Property(item => item.TokenId).HasMaxLength(32).IsRequired();
        virtualNetworkBan.Property(item => item.NodeName).HasMaxLength(256).IsRequired();
        virtualNetworkBan.Property(item => item.Reason).HasMaxLength(512).IsRequired();
        virtualNetworkBan.HasIndex(item => new { item.NetworkId, item.VirtualAddress }).IsUnique();
        virtualNetworkBan.HasIndex(item => new { item.NetworkId, item.TokenId });
    }
}
