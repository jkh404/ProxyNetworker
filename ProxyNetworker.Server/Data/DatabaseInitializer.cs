using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProxyNetworker.Server.Management;
using ProxyNetworker.Server.Security;

namespace ProxyNetworker.Server.Data;

public sealed class DatabaseInitializer : IHostedService
{
    private const int LegacyVirtualNetworkListenPort = 51820;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly SystemSettingsOptions _systemSettingsOptions;
    private readonly NetworkDefaultsOptions _networkDefaultsOptions;

    public DatabaseInitializer(
        IServiceProvider serviceProvider,
        ILogger<DatabaseInitializer> logger,
        IOptions<SystemSettingsOptions> systemSettingsOptions,
        IOptions<NetworkDefaultsOptions> networkDefaultsOptions)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _systemSettingsOptions = systemSettingsOptions.Value;
        _networkDefaultsOptions = networkDefaultsOptions.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProxyNetworkerDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var initialPasswordGenerator = scope.ServiceProvider.GetRequiredService<InitialAdminPasswordGenerator>();
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await EnsureVirtualNetworkColumnsAsync(db, _networkDefaultsOptions, cancellationToken).ConfigureAwait(false);
        await EnsurePortTunnelColumnsAsync(db, cancellationToken).ConfigureAwait(false);
        await EnsureAccessTokenColumnsAsync(db, cancellationToken).ConfigureAwait(false);
        await EnsureVirtualNetworkSessionsAsync(db, cancellationToken).ConfigureAwait(false);
        await EnsureVirtualNetworkBansAsync(db, cancellationToken).ConfigureAwait(false);
        await EnsureSystemSettingsAsync(db, _systemSettingsOptions, cancellationToken).ConfigureAwait(false);
        await EnsureDefaultAdminAsync(db, passwordHasher, initialPasswordGenerator, _systemSettingsOptions, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("SQLite database is ready at {ConnectionString}.", db.Database.GetConnectionString());
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static async Task EnsureVirtualNetworkColumnsAsync(
        ProxyNetworkerDbContext db,
        NetworkDefaultsOptions networkDefaults,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(db, "VirtualNetworks", "ListenPort", cancellationToken).ConfigureAwait(false))
        {
            var sql = $"""ALTER TABLE "VirtualNetworks" ADD COLUMN "ListenPort" INTEGER NOT NULL DEFAULT {LegacyVirtualNetworkListenPort};""";
            await db.Database.ExecuteSqlRawAsync(
                sql,
                cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(db, "VirtualNetworks", "Mtu", cancellationToken).ConfigureAwait(false))
        {
            var sql = $"""ALTER TABLE "VirtualNetworks" ADD COLUMN "Mtu" INTEGER NOT NULL DEFAULT {networkDefaults.VirtualNetworkMtu};""";
            await db.Database.ExecuteSqlRawAsync(
                sql,
                cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(db, "VirtualNetworks", "TransportProtocol", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "VirtualNetworks" ADD COLUMN "TransportProtocol" TEXT NOT NULL DEFAULT 'udp';""",
                cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(db, "VirtualNetworks", "IsEnabled", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "VirtualNetworks" ADD COLUMN "IsEnabled" INTEGER NOT NULL DEFAULT 1;""",
                cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(db, "VirtualNetworks", "CreatedAtUnixMs", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "VirtualNetworks" ADD COLUMN "CreatedAtUnixMs" INTEGER NOT NULL DEFAULT 0;""",
                cancellationToken).ConfigureAwait(false);
        }

        // KCP support was removed after relay benchmarks showed unacceptable tail latency.
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "VirtualNetworks"
            SET "IsEnabled" = 0
            WHERE LOWER(TRIM("TransportProtocol")) = 'kcp';
            """,
            cancellationToken).ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "VirtualNetworks"
            SET "CreatedAtUnixMs" = CAST(ROUND((julianday("CreatedAt") - 2440587.5) * 86400000.0) AS INTEGER)
            WHERE "CreatedAtUnixMs" = 0 AND "CreatedAt" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "IX_VirtualNetworks_CreatedAtUnixMs" ON "VirtualNetworks" ("CreatedAtUnixMs");
            CREATE INDEX IF NOT EXISTS "IX_VirtualNetworks_OwnerUserId_CreatedAtUnixMs"
                ON "VirtualNetworks" ("OwnerUserId", "CreatedAtUnixMs");
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsurePortTunnelColumnsAsync(
        ProxyNetworkerDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(db, "PortTunnelDefinitions", "CreatedAtUnixMs", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "PortTunnelDefinitions" ADD COLUMN "CreatedAtUnixMs" INTEGER NOT NULL DEFAULT 0;""",
                cancellationToken).ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "PortTunnelDefinitions"
            SET "CreatedAtUnixMs" = CAST(ROUND((julianday("CreatedAt") - 2440587.5) * 86400000.0) AS INTEGER)
            WHERE "CreatedAtUnixMs" = 0 AND "CreatedAt" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "IX_PortTunnelDefinitions_CreatedAtUnixMs" ON "PortTunnelDefinitions" ("CreatedAtUnixMs");
            CREATE INDEX IF NOT EXISTS "IX_PortTunnelDefinitions_OwnerUserId_CreatedAtUnixMs"
                ON "PortTunnelDefinitions" ("OwnerUserId", "CreatedAtUnixMs");
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureVirtualNetworkBansAsync(ProxyNetworkerDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "VirtualNetworkNodeBans" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_VirtualNetworkNodeBans" PRIMARY KEY,
                "NetworkId" TEXT NOT NULL,
                "VirtualAddress" TEXT NOT NULL,
                "TokenId" TEXT NOT NULL,
                "NodeName" TEXT NOT NULL,
                "Reason" TEXT NOT NULL,
                "CreatedAtUnixMs" INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_VirtualNetworkNodeBans_NetworkId_VirtualAddress"
                ON "VirtualNetworkNodeBans" ("NetworkId", "VirtualAddress");
            CREATE INDEX IF NOT EXISTS "IX_VirtualNetworkNodeBans_NetworkId_TokenId"
                ON "VirtualNetworkNodeBans" ("NetworkId", "TokenId");
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureAccessTokenColumnsAsync(
        ProxyNetworkerDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await ColumnExistsAsync(db, "AccessTokens", "ProtectedTokenValue", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "AccessTokens" ADD COLUMN "ProtectedTokenValue" TEXT NULL;""",
                cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(db, "AccessTokens", "CreatedAtUnixMs", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "AccessTokens" ADD COLUMN "CreatedAtUnixMs" INTEGER NOT NULL DEFAULT 0;""",
                cancellationToken).ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "AccessTokens"
            SET "CreatedAtUnixMs" = CAST(ROUND((julianday("CreatedAt") - 2440587.5) * 86400000.0) AS INTEGER)
            WHERE "CreatedAtUnixMs" = 0 AND "CreatedAt" IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "IX_AccessTokens_CreatedAtUnixMs" ON "AccessTokens" ("CreatedAtUnixMs");
            CREATE INDEX IF NOT EXISTS "IX_AccessTokens_ScopeKind_ResourceId_CreatedAtUnixMs"
                ON "AccessTokens" ("ScopeKind", "ResourceId", "CreatedAtUnixMs");
            CREATE INDEX IF NOT EXISTS "IX_AccessTokens_ScopeKind_ResourceId_CreatedAtUnixMs_Id"
                ON "AccessTokens" ("ScopeKind", "ResourceId", "CreatedAtUnixMs", "Id");
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureVirtualNetworkSessionsAsync(
        ProxyNetworkerDbContext db,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "VirtualNetworkSessions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_VirtualNetworkSessions" PRIMARY KEY,
                "NetworkId" TEXT NOT NULL,
                "VirtualAddress" TEXT NOT NULL,
                "TokenId" TEXT NOT NULL,
                "TokenPreview" TEXT NOT NULL,
                "NodeName" TEXT NOT NULL,
                "MachineName" TEXT NOT NULL,
                "OsDescription" TEXT NOT NULL,
                "OsArchitecture" TEXT NOT NULL,
                "ProcessArchitecture" TEXT NOT NULL,
                "ClientVersion" TEXT NOT NULL,
                "PublicEndpoint" TEXT NOT NULL,
                "LocalEndpoints" TEXT NOT NULL,
                "ConnectedAtUnixMs" INTEGER NOT NULL DEFAULT 0,
                "LastSeenAtUnixMs" INTEGER NOT NULL DEFAULT 0,
                "DisconnectedAtUnixMs" INTEGER NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        if (!await ColumnExistsAsync(db, "VirtualNetworkSessions", "ConnectedAtUnixMs", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "VirtualNetworkSessions" ADD COLUMN "ConnectedAtUnixMs" INTEGER NOT NULL DEFAULT 0;""",
                cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(db, "VirtualNetworkSessions", "LastSeenAtUnixMs", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "VirtualNetworkSessions" ADD COLUMN "LastSeenAtUnixMs" INTEGER NOT NULL DEFAULT 0;""",
                cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(db, "VirtualNetworkSessions", "DisconnectedAtUnixMs", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "VirtualNetworkSessions" ADD COLUMN "DisconnectedAtUnixMs" INTEGER NULL;""",
                cancellationToken).ConfigureAwait(false);
        }

        if (await ColumnExistsAsync(db, "VirtualNetworkSessions", "ConnectedAt", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE "VirtualNetworkSessions"
                SET "ConnectedAtUnixMs" = CAST(ROUND((julianday("ConnectedAt") - 2440587.5) * 86400000.0) AS INTEGER)
                WHERE "ConnectedAtUnixMs" = 0 AND "ConnectedAt" IS NOT NULL;
                """,
                cancellationToken).ConfigureAwait(false);
        }

        if (await ColumnExistsAsync(db, "VirtualNetworkSessions", "LastSeenAt", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE "VirtualNetworkSessions"
                SET "LastSeenAtUnixMs" = CAST(ROUND((julianday("LastSeenAt") - 2440587.5) * 86400000.0) AS INTEGER)
                WHERE "LastSeenAtUnixMs" = 0 AND "LastSeenAt" IS NOT NULL;
                """,
                cancellationToken).ConfigureAwait(false);
        }

        if (await ColumnExistsAsync(db, "VirtualNetworkSessions", "DisconnectedAt", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE "VirtualNetworkSessions"
                SET "DisconnectedAtUnixMs" = CAST(ROUND((julianday("DisconnectedAt") - 2440587.5) * 86400000.0) AS INTEGER)
                WHERE "DisconnectedAtUnixMs" IS NULL AND "DisconnectedAt" IS NOT NULL;
                """,
                cancellationToken).ConfigureAwait(false);
        }

        if (await ColumnExistsAsync(db, "VirtualNetworkSessions", "ConnectedAt", cancellationToken).ConfigureAwait(false))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                DROP TABLE IF EXISTS "VirtualNetworkSessions_v2";
                CREATE TABLE "VirtualNetworkSessions_v2" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_VirtualNetworkSessions" PRIMARY KEY,
                    "NetworkId" TEXT NOT NULL,
                    "VirtualAddress" TEXT NOT NULL,
                    "TokenId" TEXT NOT NULL,
                    "TokenPreview" TEXT NOT NULL,
                    "NodeName" TEXT NOT NULL,
                    "MachineName" TEXT NOT NULL,
                    "OsDescription" TEXT NOT NULL,
                    "OsArchitecture" TEXT NOT NULL,
                    "ProcessArchitecture" TEXT NOT NULL,
                    "ClientVersion" TEXT NOT NULL,
                    "PublicEndpoint" TEXT NOT NULL,
                    "LocalEndpoints" TEXT NOT NULL,
                    "ConnectedAtUnixMs" INTEGER NOT NULL DEFAULT 0,
                    "LastSeenAtUnixMs" INTEGER NOT NULL DEFAULT 0,
                    "DisconnectedAtUnixMs" INTEGER NULL
                );
                INSERT INTO "VirtualNetworkSessions_v2" (
                    "Id", "NetworkId", "VirtualAddress", "TokenId", "TokenPreview",
                    "NodeName", "MachineName", "OsDescription", "OsArchitecture",
                    "ProcessArchitecture", "ClientVersion", "PublicEndpoint", "LocalEndpoints",
                    "ConnectedAtUnixMs", "LastSeenAtUnixMs", "DisconnectedAtUnixMs")
                SELECT
                    "Id", "NetworkId", "VirtualAddress", "TokenId", "TokenPreview",
                    "NodeName", "MachineName", "OsDescription", "OsArchitecture",
                    "ProcessArchitecture", "ClientVersion", "PublicEndpoint", "LocalEndpoints",
                    COALESCE(NULLIF("ConnectedAtUnixMs", 0), CAST(ROUND((julianday("ConnectedAt") - 2440587.5) * 86400000.0) AS INTEGER), 0),
                    COALESCE(NULLIF("LastSeenAtUnixMs", 0), CAST(ROUND((julianday("LastSeenAt") - 2440587.5) * 86400000.0) AS INTEGER), 0),
                    COALESCE("DisconnectedAtUnixMs", CASE WHEN "DisconnectedAt" IS NULL THEN NULL ELSE CAST(ROUND((julianday("DisconnectedAt") - 2440587.5) * 86400000.0) AS INTEGER) END)
                FROM "VirtualNetworkSessions";
                DROP TABLE "VirtualNetworkSessions";
                ALTER TABLE "VirtualNetworkSessions_v2" RENAME TO "VirtualNetworkSessions";
                """,
                cancellationToken).ConfigureAwait(false);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_VirtualNetworkSessions_NetworkId_DisconnectedAtUnixMs"
                ON "VirtualNetworkSessions" ("NetworkId", "DisconnectedAtUnixMs");
            CREATE INDEX IF NOT EXISTS "IX_VirtualNetworkSessions_NetworkId_LastSeenAtUnixMs"
                ON "VirtualNetworkSessions" ("NetworkId", "LastSeenAtUnixMs");
            CREATE INDEX IF NOT EXISTS "IX_VirtualNetworkSessions_NetworkId_VirtualAddress"
                ON "VirtualNetworkSessions" ("NetworkId", "VirtualAddress");
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ColumnExistsAsync(
        ProxyNetworkerDbContext db,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""PRAGMA table_info("{tableName}")""";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task EnsureSystemSettingsAsync(
        ProxyNetworkerDbContext db,
        SystemSettingsOptions options,
        CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "SystemSettings" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SystemSettings" PRIMARY KEY,
                "PublicPortRangeStart" INTEGER NOT NULL,
                "PublicPortRangeEnd" INTEGER NOT NULL,
                "MaxBandwidthLimitBytes" INTEGER NOT NULL,
                "MaxTrafficSpeedBytesPerSecond" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        if (await db.SystemSettings.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        db.SystemSettings.Add(new SystemSettingsRecord
        {
            Id = SystemSettingsIds.Default,
            PublicPortRangeStart = options.PublicPortRangeStart,
            PublicPortRangeEnd = options.PublicPortRangeEnd,
            MaxBandwidthLimitBytes = options.MaxBandwidthLimitBytes,
            MaxTrafficSpeedBytesPerSecond = options.MaxTrafficSpeedBytesPerSecond,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureDefaultAdminAsync(
        ProxyNetworkerDbContext db,
        PasswordHasher passwordHasher,
        InitialAdminPasswordGenerator initialPasswordGenerator,
        SystemSettingsOptions options,
        CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        const string username = "sky";
        var initialPassword = initialPasswordGenerator.Generate();
        db.Users.Add(new UserAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Username = username,
            PasswordHash = passwordHasher.Hash(initialPassword),
            Role = UserRoles.Admin,
            MaxVirtualNetworks = int.MaxValue,
            MaxPortTunnels = int.MaxValue,
            PortRangeStart = options.PublicPortRangeStart,
            PortRangeEnd = options.PublicPortRangeEnd,
            BandwidthLimitBytes = 0,
            MaxTrafficSpeedBytesPerSecond = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            PasswordChangedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning(
            "Initial administrator account created. Username: {Username}. Initial password: {InitialPassword}. Change this password after the first sign-in.",
            username,
            initialPassword);
    }
}
