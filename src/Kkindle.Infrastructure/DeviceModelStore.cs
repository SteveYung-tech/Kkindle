using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

/// <summary>
/// 在软件数据库中保存 USB 串码（设备身份）到用户自定义型号的映射。
/// 同一设备再次连接时会根据串码自动恢复用户设置的型号。
/// </summary>
public sealed class DeviceModelStore
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);

    public DeviceModelStore(AppPaths paths)
    {
        _paths = paths;
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _paths.Database,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS DeviceModels (
                    Serial TEXT PRIMARY KEY,
                    Model TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    /// <summary>按串码读取用户设置的型号；未设置时返回 null。</summary>
    public async Task<string?> GetModelAsync(string serial, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serial)) return null;
        var normalizedSerial = NormalizeSerial(serial);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Model FROM DeviceModels WHERE Serial = $serial;";
            command.Parameters.AddWithValue("$serial", normalizedSerial);
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    /// <summary>保存或更新某串码对应的型号；已存在映射时自动覆盖。</summary>
    public async Task SetModelAsync(string serial, string model, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var normalizedSerial = NormalizeSerial(serial);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO DeviceModels (Serial, Model, UpdatedAt)
                VALUES ($serial, $model, $updatedAt)
                ON CONFLICT(Serial) DO UPDATE SET
                    Model = excluded.Model,
                    UpdatedAt = excluded.UpdatedAt;
                """;
            command.Parameters.AddWithValue("$serial", normalizedSerial);
            command.Parameters.AddWithValue("$model", model.Trim());
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    /// <summary>删除某串码的型号映射，恢复显示设备默认名称。</summary>
    public async Task DeleteModelAsync(string serial, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serial)) return;
        var normalizedSerial = NormalizeSerial(serial);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM DeviceModels WHERE Serial = $serial;";
            command.Parameters.AddWithValue("$serial", normalizedSerial);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string NormalizeSerial(string serial) => serial.Trim().ToUpperInvariant();
}
