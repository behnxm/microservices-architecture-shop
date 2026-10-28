using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Shop.BuildingBlocks.Messaging.Data;

public static class DatabaseMigrator
{
    public static async Task MigrateWithLockAsync(
        DbContext db,
        ILogger logger,
        long lockKey,
        CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql())
        {
            await db.Database.MigrateAsync(cancellationToken);
            return;
        }

        var connection = db.Database.GetDbConnection();
        var acquired = false;
        var timeout = TimeSpan.FromSeconds(45);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            // 1. Ensure the migration history table exists first.
            // On fresh PostgreSQL instances, EF Core queries "__EFMigrationsHistory" before creating it,
            // which causes PostgreSQL to log a noisy "relation does not exist" CommandError.
            // Executing idempotent DDL ensures the table exists so queries execute cleanly with zero errors.
            await using (var ensureHistoryCmd = connection.CreateCommand())
            {
                ensureHistoryCmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                        "MigrationId" character varying(150) NOT NULL,
                        "ProductVersion" character varying(32) NOT NULL,
                        CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                    );
                    """;
                await ensureHistoryCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // 2. Acquire PostgreSQL advisory lock to ensure only one replica applies migrations at a time.
            while (!cancellationToken.IsCancellationRequested && stopwatch.Elapsed < timeout)
            {
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = $"SELECT pg_try_advisory_lock({lockKey});";
                    var result = await cmd.ExecuteScalarAsync(cancellationToken);
                    acquired = result is true || (result is bool b && b);
                }

                if (acquired)
                {
                    break;
                }

                logger.LogInformation("Migration lock {LockKey} held by another replica; waiting...", lockKey);
                await Task.Delay(500, cancellationToken);
            }

            if (!acquired)
            {
                logger.LogWarning("Advisory lock acquisition timed out for {LockKey}; attempting direct migration.", lockKey);
            }
            else
            {
                logger.LogInformation("Acquired migration advisory lock {LockKey}.", lockKey);
            }

            // 3. Inspect pending migrations safely now that the history table exists and lock is held
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count == 0)
            {
                logger.LogInformation("Database is already up to date with all migrations.");
                return;
            }

            logger.LogInformation("Applying {Count} pending database migrations: {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Database migrations applied successfully.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exception encountered during database migration. Checking if migrations are already applied.");
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count == 0)
            {
                logger.LogInformation("Database is already up to date despite migration exception.");
                return;
            }
            throw;
        }
        finally
        {
            if (acquired && connection.State == System.Data.ConnectionState.Open)
            {
                try
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"SELECT pg_advisory_unlock({lockKey});";
                    await cmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to release advisory lock {LockKey}.", lockKey);
                }
            }
        }
    }
}
