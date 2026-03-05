using ArchiveTUI.Application.Ports;
using ArchiveTUI.Domain.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ArchiveTUI.Infrastructure.Adapters;

public sealed class PostgresMetadataRepository : IDatabasePort, ISettingsPort
{
    private const string LegacyImportMigrationName = "sqlite_import_v1";
    private readonly string _connectionString;
    private readonly string _legacySqlitePath;

    public PostgresMetadataRepository(string connectionString, string legacySqlitePath)
    {
        _connectionString = connectionString;
        _legacySqlitePath = legacySqlitePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        const string schemaSql = """
            CREATE TABLE IF NOT EXISTS file_metadata (
                id BIGSERIAL PRIMARY KEY,
                source_root TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                last_modified_utc TIMESTAMPTZ NOT NULL,
                file_size BIGINT NOT NULL,
                checksum_sha256 TEXT NOT NULL,
                backup_timestamp_utc TIMESTAMPTZ NOT NULL,
                CONSTRAINT uq_file_metadata_source_relative UNIQUE (source_root, relative_path)
            );

            CREATE TABLE IF NOT EXISTS file_destination_metadata (
                id BIGSERIAL PRIMARY KEY,
                source_root TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                destination_root TEXT NOT NULL,
                last_modified_utc TIMESTAMPTZ NOT NULL,
                file_size BIGINT NOT NULL,
                checksum_sha256 TEXT NOT NULL,
                backup_timestamp_utc TIMESTAMPTZ NOT NULL,
                last_verified_utc TIMESTAMPTZ NOT NULL,
                CONSTRAINT uq_file_destination_metadata_source_relative_destination UNIQUE (source_root, relative_path, destination_root)
            );

            CREATE INDEX IF NOT EXISTS ix_file_destination_metadata_source_destination
                ON file_destination_metadata (source_root, destination_root);

            CREATE TABLE IF NOT EXISTS app_settings (
                id INT PRIMARY KEY CHECK (id = 1),
                source_root TEXT NOT NULL,
                destinations_json TEXT NOT NULL,
                sample_percent INT NOT NULL DEFAULT 2,
                age_verification_enabled BOOLEAN NOT NULL DEFAULT TRUE,
                updated_utc TIMESTAMPTZ NOT NULL
            );

            CREATE TABLE IF NOT EXISTS schema_migrations (
                name TEXT PRIMARY KEY,
                applied_utc TIMESTAMPTZ NOT NULL
            );
            """;

        await connection.ExecuteAsync(new CommandDefinition(schemaSql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await TryImportLegacySqliteAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileMetadataRecord?> GetMetadataAsync(
        string sourceRoot,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                source_root AS SourceRoot,
                relative_path AS RelativePath,
                file_size AS FileSize,
                last_modified_utc AS LastModifiedUtc,
                checksum_sha256 AS ChecksumSha256,
                backup_timestamp_utc AS BackupTimestampUtc
            FROM file_metadata
            WHERE source_root = @SourceRoot AND relative_path = @RelativePath;
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<FileMetadataRow>(
            new CommandDefinition(
                sql,
                new { SourceRoot = sourceRoot, RelativePath = relativePath },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return new FileMetadataRecord(
            row.SourceRoot,
            row.RelativePath,
            row.FileSize,
            row.LastModifiedUtc,
            row.ChecksumSha256,
            row.BackupTimestampUtc);
    }

    public async Task StoreMetadataAsync(FileMetadataRecord metadata, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO file_metadata (
                source_root,
                relative_path,
                last_modified_utc,
                file_size,
                checksum_sha256,
                backup_timestamp_utc
            ) VALUES (
                @SourceRoot,
                @RelativePath,
                @LastModifiedUtc,
                @FileSize,
                @ChecksumSha256,
                @BackupTimestampUtc
            )
            ON CONFLICT (source_root, relative_path)
            DO UPDATE SET
                last_modified_utc = EXCLUDED.last_modified_utc,
                file_size = EXCLUDED.file_size,
                checksum_sha256 = EXCLUDED.checksum_sha256,
                backup_timestamp_utc = EXCLUDED.backup_timestamp_utc;
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new
        {
            metadata.SourceRoot,
            metadata.RelativePath,
            LastModifiedUtc = ToUtc(metadata.LastModifiedUtc),
            metadata.FileSize,
            metadata.ChecksumSha256,
            BackupTimestampUtc = ToUtc(metadata.BackupTimestampUtc)
        };

        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<DestinationMetadataRecord?> GetDestinationMetadataAsync(
        string sourceRoot,
        string relativePath,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                source_root AS SourceRoot,
                relative_path AS RelativePath,
                destination_root AS DestinationRoot,
                file_size AS FileSize,
                last_modified_utc AS LastModifiedUtc,
                checksum_sha256 AS ChecksumSha256,
                backup_timestamp_utc AS BackupTimestampUtc,
                last_verified_utc AS LastVerifiedUtc
            FROM file_destination_metadata
            WHERE source_root = @SourceRoot AND relative_path = @RelativePath AND destination_root = @DestinationRoot;
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<DestinationMetadataRow>(
            new CommandDefinition(
                sql,
                new { SourceRoot = sourceRoot, RelativePath = relativePath, DestinationRoot = destinationRoot },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        return new DestinationMetadataRecord(
            row.SourceRoot,
            row.RelativePath,
            row.DestinationRoot,
            row.FileSize,
            row.LastModifiedUtc,
            row.ChecksumSha256,
            row.BackupTimestampUtc,
            row.LastVerifiedUtc);
    }

    public async Task StoreDestinationMetadataAsync(
        DestinationMetadataRecord metadata,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO file_destination_metadata (
                source_root,
                relative_path,
                destination_root,
                last_modified_utc,
                file_size,
                checksum_sha256,
                backup_timestamp_utc,
                last_verified_utc
            ) VALUES (
                @SourceRoot,
                @RelativePath,
                @DestinationRoot,
                @LastModifiedUtc,
                @FileSize,
                @ChecksumSha256,
                @BackupTimestampUtc,
                @LastVerifiedUtc
            )
            ON CONFLICT (source_root, relative_path, destination_root)
            DO UPDATE SET
                last_modified_utc = EXCLUDED.last_modified_utc,
                file_size = EXCLUDED.file_size,
                checksum_sha256 = EXCLUDED.checksum_sha256,
                backup_timestamp_utc = EXCLUDED.backup_timestamp_utc,
                last_verified_utc = EXCLUDED.last_verified_utc;
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var parameters = new
        {
            metadata.SourceRoot,
            metadata.RelativePath,
            metadata.DestinationRoot,
            metadata.FileSize,
            LastModifiedUtc = ToUtc(metadata.LastModifiedUtc),
            metadata.ChecksumSha256,
            BackupTimestampUtc = ToUtc(metadata.BackupTimestampUtc),
            LastVerifiedUtc = ToUtc(metadata.LastVerifiedUtc)
        };

        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AppSettingsRecord?> LoadAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                source_root AS SourceRoot,
                destinations_json AS DestinationsJson,
                sample_percent AS SamplePercent,
                age_verification_enabled AS AgeVerificationEnabled,
                updated_utc AS UpdatedUtc
            FROM app_settings
            WHERE id = 1;
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<AppSettingsRow>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var destinations = JsonSerializer.Deserialize<string[]>(row.DestinationsJson) ?? Array.Empty<string>();
        return new AppSettingsRecord(row.SourceRoot, destinations, row.SamplePercent, row.AgeVerificationEnabled, row.UpdatedUtc);
    }

    public async Task SaveAsync(string sourceRoot, IReadOnlyCollection<string> destinationRoots, int verifySamplePercent, bool ageVerificationEnabled, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO app_settings (id, source_root, destinations_json, sample_percent, age_verification_enabled, updated_utc)
            VALUES (1, @SourceRoot, @DestinationsJson, @SamplePercent, @AgeVerificationEnabled, @UpdatedUtc)
            ON CONFLICT (id) DO UPDATE SET
                source_root = EXCLUDED.source_root,
                destinations_json = EXCLUDED.destinations_json,
                sample_percent = EXCLUDED.sample_percent,
                age_verification_enabled = EXCLUDED.age_verification_enabled,
                updated_utc = EXCLUDED.updated_utc;
            """;

        var payload = new
        {
            SourceRoot = sourceRoot,
            DestinationsJson = JsonSerializer.Serialize(destinationRoots.ToArray()),
            SamplePercent = verifySamplePercent,
            AgeVerificationEnabled = ageVerificationEnabled,
            UpdatedUtc = DateTime.UtcNow
        };

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(sql, payload, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DestinationMetadataRecord> ListDestinationMetadataAsync(
        string sourceRoot,
        string destinationRoot,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                source_root AS SourceRoot,
                relative_path AS RelativePath,
                destination_root AS DestinationRoot,
                file_size AS FileSize,
                last_modified_utc AS LastModifiedUtc,
                checksum_sha256 AS ChecksumSha256,
                backup_timestamp_utc AS BackupTimestampUtc,
                last_verified_utc AS LastVerifiedUtc
            FROM file_destination_metadata
            WHERE source_root = @SourceRoot AND destination_root = @DestinationRoot;
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DestinationMetadataRow>(
            new CommandDefinition(sql, new { SourceRoot = sourceRoot, DestinationRoot = destinationRoot }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var row in rows)
        {
            yield return new DestinationMetadataRecord(
                row.SourceRoot,
                row.RelativePath,
                row.DestinationRoot,
                row.FileSize,
                row.LastModifiedUtc,
                row.ChecksumSha256,
                row.BackupTimestampUtc,
                row.LastVerifiedUtc);
        }
    }

    public async Task DeleteDestinationMetadataAsync(
        string sourceRoot,
        string relativePath,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM file_destination_metadata
            WHERE source_root = @SourceRoot AND relative_path = @RelativePath AND destination_root = @DestinationRoot;
            """;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { SourceRoot = sourceRoot, RelativePath = relativePath, DestinationRoot = destinationRoot },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private NpgsqlConnection CreateConnection()
    {
        return new NpgsqlConnection(_connectionString);
    }

    private async Task TryImportLegacySqliteAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var markerExists = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE name = @Name);",
                new { Name = LegacyImportMigrationName },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (markerExists)
        {
            return;
        }

        var destinationCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT COUNT(1) FROM file_destination_metadata;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        var metadataCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT COUNT(1) FROM file_metadata;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        var settingsCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT COUNT(1) FROM app_settings;", cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (destinationCount > 0 || metadataCount > 0 || settingsCount > 0)
        {
            return;
        }

        var sqlitePath = Path.GetFullPath(_legacySqlitePath);
        if (!File.Exists(sqlitePath))
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var command = new CommandDefinition(
            "INSERT INTO schema_migrations (name, applied_utc) VALUES (@Name, @AppliedUtc) ON CONFLICT (name) DO NOTHING;",
            new { Name = LegacyImportMigrationName, AppliedUtc = DateTime.UtcNow },
            transaction,
            cancellationToken: cancellationToken);

        try
        {
            await ImportLegacyFileMetadataAsync(connection, transaction, sqlitePath, cancellationToken).ConfigureAwait(false);
            await ImportLegacyDestinationMetadataAsync(connection, transaction, sqlitePath, cancellationToken).ConfigureAwait(false);
            await ImportLegacyAppSettingsAsync(connection, transaction, sqlitePath, cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(command).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ImportLegacyFileMetadataAsync(
        NpgsqlConnection pgConnection,
        IDbTransaction transaction,
        string sqlitePath,
        CancellationToken cancellationToken)
    {
        const string sqliteSql = """
            SELECT SourceRoot, RelativePath, LastModifiedUtc, FileSize, ChecksumSha256, BackupTimestampUtc
            FROM FileMetadata;
            """;

        const string pgSql = """
            INSERT INTO file_metadata (
                source_root,
                relative_path,
                last_modified_utc,
                file_size,
                checksum_sha256,
                backup_timestamp_utc
            ) VALUES (
                @SourceRoot,
                @RelativePath,
                @LastModifiedUtc,
                @FileSize,
                @ChecksumSha256,
                @BackupTimestampUtc
            )
            ON CONFLICT (source_root, relative_path) DO NOTHING;
            """;

        await using var sqliteConnection = new SqliteConnection($"Data Source={sqlitePath}");
        await sqliteConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await sqliteConnection.QueryAsync<LegacyFileMetadataRow>(
            new CommandDefinition(sqliteSql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var row in rows)
        {
            await pgConnection.ExecuteAsync(new CommandDefinition(
                pgSql,
                new
                {
                    row.SourceRoot,
                    row.RelativePath,
                    LastModifiedUtc = ToUtc(row.LastModifiedUtc),
                    row.FileSize,
                    row.ChecksumSha256,
                    BackupTimestampUtc = ToUtc(row.BackupTimestampUtc)
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task ImportLegacyDestinationMetadataAsync(
        NpgsqlConnection pgConnection,
        IDbTransaction transaction,
        string sqlitePath,
        CancellationToken cancellationToken)
    {
        const string pgSql = """
            INSERT INTO file_destination_metadata (
                source_root,
                relative_path,
                destination_root,
                last_modified_utc,
                file_size,
                checksum_sha256,
                backup_timestamp_utc,
                last_verified_utc
            ) VALUES (
                @SourceRoot,
                @RelativePath,
                @DestinationRoot,
                @LastModifiedUtc,
                @FileSize,
                @ChecksumSha256,
                @BackupTimestampUtc,
                @LastVerifiedUtc
            )
            ON CONFLICT (source_root, relative_path, destination_root) DO NOTHING;
            """;

        await using var sqliteConnection = new SqliteConnection($"Data Source={sqlitePath}");
        await sqliteConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var hasLastVerifiedColumn = await sqliteConnection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT COUNT(1) FROM pragma_table_info('FileDestinationMetadata') WHERE name = 'LastVerifiedUtc';",
                cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;

        var sqliteSql = hasLastVerifiedColumn
            ? """
              SELECT SourceRoot, RelativePath, DestinationRoot, LastModifiedUtc, FileSize, ChecksumSha256, BackupTimestampUtc, LastVerifiedUtc
              FROM FileDestinationMetadata;
              """
            : """
              SELECT SourceRoot, RelativePath, DestinationRoot, LastModifiedUtc, FileSize, ChecksumSha256, BackupTimestampUtc, BackupTimestampUtc AS LastVerifiedUtc
              FROM FileDestinationMetadata;
              """;

        var rows = await sqliteConnection.QueryAsync<LegacyDestinationMetadataRow>(
            new CommandDefinition(sqliteSql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var row in rows)
        {
            await pgConnection.ExecuteAsync(new CommandDefinition(
                pgSql,
                new
                {
                    row.SourceRoot,
                    row.RelativePath,
                    row.DestinationRoot,
                    LastModifiedUtc = ToUtc(row.LastModifiedUtc),
                    row.FileSize,
                    row.ChecksumSha256,
                    BackupTimestampUtc = ToUtc(row.BackupTimestampUtc),
                    LastVerifiedUtc = ToUtc(row.LastVerifiedUtc)
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task ImportLegacyAppSettingsAsync(
        NpgsqlConnection pgConnection,
        IDbTransaction transaction,
        string sqlitePath,
        CancellationToken cancellationToken)
    {
        const string pgSql = """
            INSERT INTO app_settings (id, source_root, destinations_json, sample_percent, age_verification_enabled, updated_utc)
            VALUES (1, @SourceRoot, @DestinationsJson, @SamplePercent, @AgeVerificationEnabled, @UpdatedUtc)
            ON CONFLICT (id) DO UPDATE SET
                source_root = EXCLUDED.source_root,
                destinations_json = EXCLUDED.destinations_json,
                sample_percent = EXCLUDED.sample_percent,
                age_verification_enabled = EXCLUDED.age_verification_enabled,
                updated_utc = EXCLUDED.updated_utc;
            """;

        await using var sqliteConnection = new SqliteConnection($"Data Source={sqlitePath}");
        await sqliteConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var columns = (await sqliteConnection.QueryAsync<string>(
            new CommandDefinition(
                "SELECT name FROM pragma_table_info('AppSettings');",
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hasSamplePercent = columns.Contains("SamplePercent");
        var hasAgeVerificationEnabled = columns.Contains("AgeVerificationEnabled");

        var sqliteSql = $"""
            SELECT Id, SourceRoot, DestinationsJson,
                   {(hasSamplePercent ? "SamplePercent" : "2 AS SamplePercent")},
                   {(hasAgeVerificationEnabled ? "AgeVerificationEnabled" : "1 AS AgeVerificationEnabled")},
                   UpdatedUtc
            FROM AppSettings
            WHERE Id = 1;
            """;

        var row = await sqliteConnection.QuerySingleOrDefaultAsync<LegacyAppSettingsRow>(
            new CommandDefinition(sqliteSql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (row is null)
        {
            return;
        }

        await pgConnection.ExecuteAsync(new CommandDefinition(
            pgSql,
            new
            {
                row.SourceRoot,
                row.DestinationsJson,
                SamplePercent = row.SamplePercent ?? 2,
                AgeVerificationEnabled = (row.AgeVerificationEnabled ?? 1) != 0,
                UpdatedUtc = ToUtc(row.UpdatedUtc)
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private sealed class FileMetadataRow
    {
        public required string SourceRoot { get; init; }
        public required string RelativePath { get; init; }
        public long FileSize { get; init; }
        public DateTime LastModifiedUtc { get; init; }
        public required string ChecksumSha256 { get; init; }
        public DateTime BackupTimestampUtc { get; init; }
    }

    private sealed class DestinationMetadataRow
    {
        public required string SourceRoot { get; init; }
        public required string RelativePath { get; init; }
        public required string DestinationRoot { get; init; }
        public long FileSize { get; init; }
        public DateTime LastModifiedUtc { get; init; }
        public required string ChecksumSha256 { get; init; }
        public DateTime BackupTimestampUtc { get; init; }
        public DateTime LastVerifiedUtc { get; init; }
    }

    private sealed class AppSettingsRow
    {
        public required string SourceRoot { get; init; }
        public required string DestinationsJson { get; init; }
        public int SamplePercent { get; init; }
        public bool AgeVerificationEnabled { get; init; }
        public DateTime UpdatedUtc { get; init; }
    }

    private sealed class LegacyFileMetadataRow
    {
        public required string SourceRoot { get; init; }
        public required string RelativePath { get; init; }
        public DateTime LastModifiedUtc { get; init; }
        public long FileSize { get; init; }
        public required string ChecksumSha256 { get; init; }
        public DateTime BackupTimestampUtc { get; init; }
    }

    private sealed class LegacyDestinationMetadataRow
    {
        public required string SourceRoot { get; init; }
        public required string RelativePath { get; init; }
        public required string DestinationRoot { get; init; }
        public DateTime LastModifiedUtc { get; init; }
        public long FileSize { get; init; }
        public required string ChecksumSha256 { get; init; }
        public DateTime BackupTimestampUtc { get; init; }
        public DateTime LastVerifiedUtc { get; init; }
    }

    private sealed class LegacyAppSettingsRow
    {
        public int Id { get; init; }
        public required string SourceRoot { get; init; }
        public required string DestinationsJson { get; init; }
        public int? SamplePercent { get; init; }
        public int? AgeVerificationEnabled { get; init; }
        public DateTime UpdatedUtc { get; init; }
    }
}
