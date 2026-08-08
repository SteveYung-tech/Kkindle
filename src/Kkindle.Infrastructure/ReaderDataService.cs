using System.Text.RegularExpressions;
using Kkindle.Core;
using Microsoft.Data.Sqlite;

namespace Kkindle.Infrastructure;

public sealed partial class ReaderDataService
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private bool _ftsAvailable;

    public ReaderDataService(AppPaths paths)
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
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS ReaderAnnotations (
                    Id TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    Fragment TEXT NULL,
                    StartOffset INTEGER NOT NULL,
                    EndOffset INTEGER NOT NULL,
                    SelectedText TEXT NOT NULL,
                    Prefix TEXT NOT NULL DEFAULT '',
                    Suffix TEXT NOT NULL DEFAULT '',
                    Color TEXT NOT NULL DEFAULT '#000000',
                    Note TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ReaderAnnotations_BookFile
                    ON ReaderAnnotations(BookFileId, ChapterPath, StartOffset);

                CREATE TABLE IF NOT EXISTS BookContentChunks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    SourceHash TEXT NOT NULL,
                    ChapterIndex INTEGER NOT NULL,
                    ChunkIndex INTEGER NOT NULL,
                    ChapterTitle TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    StartOffset INTEGER NOT NULL,
                    EndOffset INTEGER NOT NULL,
                    Content TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS UX_BookContentChunks_Position
                    ON BookContentChunks(BookFileId, SourceHash, ChapterIndex, ChunkIndex);
                CREATE INDEX IF NOT EXISTS IX_BookContentChunks_Book
                    ON BookContentChunks(BookId, ChapterIndex, ChunkIndex);

                CREATE TABLE IF NOT EXISTS ReaderProgress (
                    BookFileId TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    Fragment TEXT NULL,
                    ChapterIndex INTEGER NOT NULL DEFAULT 0,
                    ScrollPosition INTEGER NOT NULL DEFAULT 0,
                    ProgressPercent REAL NOT NULL DEFAULT 0,
                    FlowMode INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ReaderBookmarks (
                    Id TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    Fragment TEXT NULL,
                    ChapterIndex INTEGER NOT NULL DEFAULT 0,
                    Title TEXT NOT NULL DEFAULT '',
                    Quote TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ReaderBookmarks_BookFile
                    ON ReaderBookmarks(BookFileId, ChapterIndex, CreatedAt);

                CREATE TABLE IF NOT EXISTS ReaderLayoutSettings (
                    BookFileId TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    FontScale REAL NOT NULL DEFAULT 1.0,
                    LineHeight REAL NOT NULL DEFAULT 1.88,
                    MaxWidth REAL NOT NULL DEFAULT 800,
                    BodyPadding REAL NOT NULL DEFAULT 68,
                    FontFamily TEXT NULL,
                    FlowMode INTEGER NOT NULL DEFAULT 0,
                    VerticalWriting INTEGER NOT NULL DEFAULT 0,
                    TwoPageMode INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ReaderReadingStats (
                    BookFileId TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    CumulativeSeconds INTEGER NOT NULL DEFAULT 0,
                    ProgressPercent REAL NOT NULL DEFAULT 0,
                    CompletedChapters INTEGER NOT NULL DEFAULT 0,
                    TotalChapters INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureReaderLayoutTwoPageColumnAsync(connection, cancellationToken);

            _ftsAvailable = await EnsureFullTextIndexAsync(connection, cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<IReadOnlyList<ReaderAnnotation>> GetAnnotationsAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                   SelectedText, Prefix, Suffix, Color, Note, CreatedAt, UpdatedAt
            FROM ReaderAnnotations
            WHERE BookFileId = $bookFileId
            ORDER BY ChapterPath COLLATE NOCASE, StartOffset, CreatedAt;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        var result = new List<ReaderAnnotation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReaderAnnotation
            {
                Id = Guid.Parse(reader.GetString(0)),
                BookId = Guid.Parse(reader.GetString(1)),
                BookFileId = Guid.Parse(reader.GetString(2)),
                ChapterPath = reader.GetString(3),
                Fragment = reader.IsDBNull(4) ? null : reader.GetString(4),
                StartOffset = reader.GetInt32(5),
                EndOffset = reader.GetInt32(6),
                SelectedText = reader.GetString(7),
                Prefix = reader.GetString(8),
                Suffix = reader.GetString(9),
                Color = reader.GetString(10),
                Note = reader.GetString(11),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(12)),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(13))
            });
        }
        return result;
    }

    public async Task SaveAnnotationAsync(ReaderAnnotation annotation, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderAnnotations (
                    Id, BookId, BookFileId, ChapterPath, Fragment, StartOffset, EndOffset,
                    SelectedText, Prefix, Suffix, Color, Note, CreatedAt, UpdatedAt)
                VALUES (
                    $id, $bookId, $bookFileId, $chapterPath, $fragment, $startOffset, $endOffset,
                    $selectedText, $prefix, $suffix, $color, $note, $createdAt, $updatedAt)
                ON CONFLICT(Id) DO UPDATE SET
                    ChapterPath=$chapterPath, Fragment=$fragment, StartOffset=$startOffset, EndOffset=$endOffset,
                    SelectedText=$selectedText, Prefix=$prefix, Suffix=$suffix, Color=$color,
                    Note=$note, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$id", annotation.Id.ToString());
            command.Parameters.AddWithValue("$bookId", annotation.BookId.ToString());
            command.Parameters.AddWithValue("$bookFileId", annotation.BookFileId.ToString());
            command.Parameters.AddWithValue("$chapterPath", annotation.ChapterPath);
            command.Parameters.AddWithValue("$fragment", (object?)annotation.Fragment ?? DBNull.Value);
            command.Parameters.AddWithValue("$startOffset", annotation.StartOffset);
            command.Parameters.AddWithValue("$endOffset", annotation.EndOffset);
            command.Parameters.AddWithValue("$selectedText", annotation.SelectedText);
            command.Parameters.AddWithValue("$prefix", annotation.Prefix);
            command.Parameters.AddWithValue("$suffix", annotation.Suffix);
            command.Parameters.AddWithValue("$color", annotation.Color);
            command.Parameters.AddWithValue("$note", annotation.Note);
            command.Parameters.AddWithValue("$createdAt", annotation.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", annotation.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task DeleteAnnotationAsync(Guid annotationId, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ReaderAnnotations WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", annotationId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Reading progress (breakpoint restore).
    // ------------------------------------------------------------------

    public async Task<ReaderProgressRow?> GetProgressAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                   ProgressPercent, FlowMode, UpdatedAt
            FROM ReaderProgress
            WHERE BookFileId = $bookFileId;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReaderProgressRow(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetDouble(6),
            reader.GetInt32(7),
            DateTimeOffset.Parse(reader.GetString(8)));
    }

    public async Task SaveProgressAsync(
        ReaderProgressRow progress,
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderProgress (
                    BookFileId, BookId, ChapterPath, Fragment, ChapterIndex, ScrollPosition,
                    ProgressPercent, FlowMode, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $chapterPath, $fragment, $chapterIndex, $scrollPosition,
                    $progressPercent, $flowMode, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    BookId=$bookId, ChapterPath=$chapterPath, Fragment=$fragment,
                    ChapterIndex=$chapterIndex, ScrollPosition=$scrollPosition,
                    ProgressPercent=$progressPercent, FlowMode=$flowMode, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", progress.BookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", progress.BookId.ToString());
            command.Parameters.AddWithValue("$chapterPath", progress.ChapterPath);
            command.Parameters.AddWithValue("$fragment", (object?)progress.Fragment ?? DBNull.Value);
            command.Parameters.AddWithValue("$chapterIndex", progress.ChapterIndex);
            command.Parameters.AddWithValue("$scrollPosition", progress.ScrollPosition);
            command.Parameters.AddWithValue("$progressPercent", progress.ProgressPercent);
            command.Parameters.AddWithValue("$flowMode", progress.FlowMode);
            command.Parameters.AddWithValue("$updatedAt", progress.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Bookmarks.
    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<ReaderBookmark>> GetBookmarksAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, Title, Quote, CreatedAt
            FROM ReaderBookmarks
            WHERE BookFileId = $bookFileId
            ORDER BY ChapterIndex, CreatedAt;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        var result = new List<ReaderBookmark>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReaderBookmark
            {
                Id = Guid.Parse(reader.GetString(0)),
                BookId = Guid.Parse(reader.GetString(1)),
                BookFileId = Guid.Parse(reader.GetString(2)),
                ChapterPath = reader.GetString(3),
                Fragment = reader.IsDBNull(4) ? null : reader.GetString(4),
                ChapterIndex = reader.GetInt32(5),
                Title = reader.GetString(6),
                Quote = reader.GetString(7),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(8))
            });
        }
        return result;
    }

    public async Task SaveBookmarkAsync(ReaderBookmark bookmark, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderBookmarks (
                    Id, BookId, BookFileId, ChapterPath, Fragment, ChapterIndex, Title, Quote, CreatedAt)
                VALUES (
                    $id, $bookId, $bookFileId, $chapterPath, $fragment, $chapterIndex, $title, $quote, $createdAt)
                ON CONFLICT(Id) DO UPDATE SET
                    BookId=$bookId, BookFileId=$bookFileId, ChapterPath=$chapterPath, Fragment=$fragment,
                    ChapterIndex=$chapterIndex, Title=$title, Quote=$quote, CreatedAt=$createdAt;
                """;
            command.Parameters.AddWithValue("$id", bookmark.Id.ToString());
            command.Parameters.AddWithValue("$bookId", bookmark.BookId.ToString());
            command.Parameters.AddWithValue("$bookFileId", bookmark.BookFileId.ToString());
            command.Parameters.AddWithValue("$chapterPath", bookmark.ChapterPath);
            command.Parameters.AddWithValue("$fragment", (object?)bookmark.Fragment ?? DBNull.Value);
            command.Parameters.AddWithValue("$chapterIndex", bookmark.ChapterIndex);
            command.Parameters.AddWithValue("$title", bookmark.Title);
            command.Parameters.AddWithValue("$quote", bookmark.Quote);
            command.Parameters.AddWithValue("$createdAt", bookmark.CreatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task DeleteBookmarkAsync(Guid bookmarkId, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ReaderBookmarks WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", bookmarkId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Per-book layout settings.
    // ------------------------------------------------------------------

    private static async Task EnsureReaderLayoutTwoPageColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(ReaderLayoutSettings);";
        var hasTwoPageColumn = false;
        await using (var reader = await inspect.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.FieldCount > 1
                    && string.Equals(reader.GetString(1), "TwoPageMode", StringComparison.OrdinalIgnoreCase))
                {
                    hasTwoPageColumn = true;
                    break;
                }
            }
        }

        if (hasTwoPageColumn) return;

        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE ReaderLayoutSettings ADD COLUMN TwoPageMode INTEGER NOT NULL DEFAULT 0;";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ReaderLayoutSettings?> GetLayoutSettingsAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FontScale, LineHeight, MaxWidth, BodyPadding, FontFamily, FlowMode, VerticalWriting, TwoPageMode
            FROM ReaderLayoutSettings
            WHERE BookFileId = $bookFileId;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReaderLayoutSettings(
            reader.GetDouble(0),
            reader.GetDouble(1),
            reader.GetDouble(2),
            reader.GetDouble(3),
            reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6) != 0,
            reader.GetInt32(7) != 0);
    }

    public async Task SaveLayoutSettingsAsync(
        Guid bookId,
        Guid bookFileId,
        ReaderLayoutSettings settings,
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderLayoutSettings (
                    BookFileId, BookId, FontScale, LineHeight, MaxWidth, BodyPadding,
                    FontFamily, FlowMode, VerticalWriting, TwoPageMode, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $fontScale, $lineHeight, $maxWidth, $bodyPadding,
                    $fontFamily, $flowMode, $verticalWriting, $twoPageMode, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    BookId=$bookId, FontScale=$fontScale, LineHeight=$lineHeight,
                    MaxWidth=$maxWidth, BodyPadding=$bodyPadding, FontFamily=$fontFamily,
                    FlowMode=$flowMode, VerticalWriting=$verticalWriting,
                    TwoPageMode=$twoPageMode, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", bookId.ToString());
            command.Parameters.AddWithValue("$fontScale", settings.FontScale);
            command.Parameters.AddWithValue("$lineHeight", settings.LineHeight);
            command.Parameters.AddWithValue("$maxWidth", settings.MaxWidth);
            command.Parameters.AddWithValue("$bodyPadding", settings.BodyPadding);
            command.Parameters.AddWithValue("$fontFamily", string.IsNullOrWhiteSpace(settings.FontFamily) ? DBNull.Value : settings.FontFamily);
            command.Parameters.AddWithValue("$flowMode", settings.FlowMode);
            command.Parameters.AddWithValue("$verticalWriting", settings.VerticalWriting ? 1 : 0);
            command.Parameters.AddWithValue("$twoPageMode", settings.TwoPageMode ? 1 : 0);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    // ------------------------------------------------------------------
    // Reading stats (cumulative active reading time + progress snapshot).
    // ------------------------------------------------------------------

    public async Task<ReaderReadingStats?> GetReadingStatsAsync(
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BookId, BookFileId, CumulativeSeconds, ProgressPercent, CompletedChapters, TotalChapters, UpdatedAt
            FROM ReaderReadingStats
            WHERE BookFileId = $bookFileId;
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReaderReadingStats
        {
            BookId = Guid.Parse(reader.GetString(0)),
            BookFileId = Guid.Parse(reader.GetString(1)),
            CumulativeSeconds = reader.GetInt64(2),
            ProgressPercent = reader.GetDouble(3),
            CompletedChapters = reader.GetInt32(4),
            TotalChapters = reader.GetInt32(5),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
        };
    }

    public async Task SaveReadingStatsAsync(ReaderReadingStats stats, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderReadingStats (
                    BookFileId, BookId, CumulativeSeconds, ProgressPercent, CompletedChapters, TotalChapters, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $cumulativeSeconds, $progressPercent, $completedChapters, $totalChapters, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    BookId=$bookId, CumulativeSeconds=$cumulativeSeconds, ProgressPercent=$progressPercent,
                    CompletedChapters=$completedChapters, TotalChapters=$totalChapters, UpdatedAt=$updatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", stats.BookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", stats.BookId.ToString());
            command.Parameters.AddWithValue("$cumulativeSeconds", stats.CumulativeSeconds);
            command.Parameters.AddWithValue("$progressPercent", stats.ProgressPercent);
            command.Parameters.AddWithValue("$completedChapters", stats.CompletedChapters);
            command.Parameters.AddWithValue("$totalChapters", stats.TotalChapters);
            command.Parameters.AddWithValue("$updatedAt", stats.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task AddReadingTimeAsync(
        Guid bookId,
        Guid bookFileId,
        long activeSeconds,
        double progressPercent,
        int completedChapters,
        int totalChapters,
        CancellationToken cancellationToken = default)
    {
        if (activeSeconds <= 0) return;
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ReaderReadingStats (
                    BookFileId, BookId, CumulativeSeconds, ProgressPercent, CompletedChapters, TotalChapters, UpdatedAt)
                VALUES (
                    $bookFileId, $bookId, $seconds, $progressPercent, $completedChapters, $totalChapters, $updatedAt)
                ON CONFLICT(BookFileId) DO UPDATE SET
                    CumulativeSeconds = CumulativeSeconds + $seconds,
                    ProgressPercent = $progressPercent,
                    CompletedChapters = $completedChapters,
                    TotalChapters = $totalChapters,
                    UpdatedAt = $updatedAt;
                """;
            command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            command.Parameters.AddWithValue("$bookId", bookId.ToString());
            command.Parameters.AddWithValue("$seconds", activeSeconds);
            command.Parameters.AddWithValue("$progressPercent", progressPercent);
            command.Parameters.AddWithValue("$completedChapters", completedChapters);
            command.Parameters.AddWithValue("$totalChapters", totalChapters);
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<bool> IsIndexCurrentAsync(
        Guid bookFileId,
        string sourceHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM BookContentChunks
                WHERE BookFileId = $bookFileId AND SourceHash = $sourceHash LIMIT 1);
            """;
        command.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
        command.Parameters.AddWithValue("$sourceHash", sourceHash);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task ReplaceBookChunksAsync(
        Guid bookId,
        Guid bookFileId,
        string sourceHash,
        IReadOnlyList<BookContentChunkDraft> chunks,
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM BookContentChunks WHERE BookFileId = $bookFileId;";
            delete.Parameters.AddWithValue("$bookFileId", bookFileId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);

            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO BookContentChunks (
                    BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                    ChapterPath, StartOffset, EndOffset, Content)
                VALUES (
                    $bookId, $bookFileId, $sourceHash, $chapterIndex, $chunkIndex, $chapterTitle,
                    $chapterPath, $startOffset, $endOffset, $content);
                """;
            insert.Parameters.Add("$bookId", SqliteType.Text);
            insert.Parameters.Add("$bookFileId", SqliteType.Text);
            insert.Parameters.Add("$sourceHash", SqliteType.Text);
            insert.Parameters.Add("$chapterIndex", SqliteType.Integer);
            insert.Parameters.Add("$chunkIndex", SqliteType.Integer);
            insert.Parameters.Add("$chapterTitle", SqliteType.Text);
            insert.Parameters.Add("$chapterPath", SqliteType.Text);
            insert.Parameters.Add("$startOffset", SqliteType.Integer);
            insert.Parameters.Add("$endOffset", SqliteType.Integer);
            insert.Parameters.Add("$content", SqliteType.Text);

            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                insert.Parameters["$bookId"].Value = bookId.ToString();
                insert.Parameters["$bookFileId"].Value = bookFileId.ToString();
                insert.Parameters["$sourceHash"].Value = sourceHash;
                insert.Parameters["$chapterIndex"].Value = chunk.ChapterIndex;
                insert.Parameters["$chunkIndex"].Value = chunk.ChunkIndex;
                insert.Parameters["$chapterTitle"].Value = chunk.ChapterTitle;
                insert.Parameters["$chapterPath"].Value = chunk.ChapterPath;
                insert.Parameters["$startOffset"].Value = chunk.StartOffset;
                insert.Parameters["$endOffset"].Value = chunk.EndOffset;
                insert.Parameters["$content"].Value = chunk.Content;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<IReadOnlyList<BookContentChunk>> SearchBookAsync(
        Guid bookId,
        string query,
        int limit = 6,
        CancellationToken cancellationToken = default)
    {
        var terms = BuildSearchTerms(query);
        if (terms.Count == 0) return [];

        if (_ftsAvailable && terms.Any(term => term.Length >= 3))
        {
            try
            {
                var ftsResults = await SearchFullTextAsync(bookId, terms, limit, cancellationToken);
                if (ftsResults.Count > 0) return ftsResults;
            }
            catch (SqliteException)
            {
                // Malformed or unsupported FTS query: the LIKE fallback remains fully local.
            }
        }

        return await SearchLikeAsync(bookId, terms, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<BookContentChunk>> GetBookOverviewChunksAsync(
        Guid bookId,
        int limit = 12,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                   ChapterPath, StartOffset, EndOffset, Content
            FROM BookContentChunks
            WHERE BookId = $bookId AND ChunkIndex = 0
            ORDER BY ChapterIndex;
            """;
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        var openings = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) openings.Add(ReadChunk(reader));
        if (openings.Count <= limit) return openings;

        var sampled = new List<BookContentChunk>(limit);
        for (var index = 0; index < limit; index++)
        {
            var sourceIndex = (int)Math.Round(index * (openings.Count - 1d) / (limit - 1d));
            sampled.Add(openings[sourceIndex]);
        }
        return sampled;
    }

    private async Task<IReadOnlyList<BookContentChunk>> SearchFullTextAsync(
        Guid bookId,
        IReadOnlyList<string> terms,
        int limit,
        CancellationToken cancellationToken)
    {
        var searchable = terms.Where(term => term.Length >= 3).Take(16).ToArray();
        if (searchable.Length == 0) return [];
        var match = string.Join(" OR ", searchable.Select(term => $"\"{term.Replace("\"", "\"\"")}\""));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.Id, c.BookId, c.BookFileId, c.SourceHash, c.ChapterIndex, c.ChunkIndex,
                   c.ChapterTitle, c.ChapterPath, c.StartOffset, c.EndOffset, c.Content,
                   bm25(BookContentFts, 1.0, 2.8) AS Rank
            FROM BookContentFts
            INNER JOIN BookContentChunks c ON c.Id = BookContentFts.rowid
            WHERE BookContentFts MATCH $query AND c.BookId = $bookId
            ORDER BY Rank, c.ChapterIndex, c.ChunkIndex
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", match);
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20));
        var result = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadChunk(reader, includeRank: true));
        return result;
    }

    private async Task<IReadOnlyList<BookContentChunk>> SearchLikeAsync(
        Guid bookId,
        IReadOnlyList<string> terms,
        int limit,
        CancellationToken cancellationToken)
    {
        var selectedTerms = terms.OrderByDescending(term => term.Length).Take(5).ToArray();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        var predicates = new List<string>();
        for (var index = 0; index < selectedTerms.Length; index++)
        {
            predicates.Add($"(Content LIKE $term{index} OR ChapterTitle LIKE $term{index})");
            command.Parameters.AddWithValue($"$term{index}", $"%{selectedTerms[index]}%");
        }
        command.CommandText = $"""
            SELECT Id, BookId, BookFileId, SourceHash, ChapterIndex, ChunkIndex, ChapterTitle,
                   ChapterPath, StartOffset, EndOffset, Content
            FROM BookContentChunks
            WHERE BookId = $bookId AND ({string.Join(" OR ", predicates)})
            ORDER BY ChapterIndex, ChunkIndex
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20));
        var result = new List<BookContentChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadChunk(reader));
        return result;
    }

    internal static IReadOnlyList<string> BuildSearchTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var normalized = WhitespaceRegex().Replace(query.Trim(), " ");
        foreach (var stopPhrase in ChineseStopPhrases)
            normalized = normalized.Replace(stopPhrase, string.Empty, StringComparison.OrdinalIgnoreCase);

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in LatinWordRegex().Matches(normalized))
            if (match.Value.Length >= 3) terms.Add(match.Value);

        foreach (Match match in ChineseSequenceRegex().Matches(normalized))
        {
            var value = match.Value;
            if (value.Length >= 3) terms.Add(value.Length <= 18 ? value : value[..18]);
            for (var size = Math.Min(6, value.Length); size >= 3; size--)
            {
                for (var start = 0; start + size <= value.Length; start += Math.Max(1, size - 2))
                    terms.Add(value.Substring(start, size));
            }
        }

        if (terms.Count == 0)
        {
            var fallback = normalized.Trim();
            if (fallback.Length > 0) terms.Add(fallback);
        }
        return terms.OrderByDescending(term => term.Length).Take(24).ToArray();
    }

    private async Task<bool> EnsureFullTextIndexAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existence = connection.CreateCommand();
        existence.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'BookContentFts');";
        var existed = Convert.ToInt32(await existence.ExecuteScalarAsync(cancellationToken)) == 1;
        try
        {
            await CreateFtsTableAsync(connection, "trigram", cancellationToken);
        }
        catch (SqliteException)
        {
            try
            {
                await CreateFtsTableAsync(connection, "unicode61 remove_diacritics 2", cancellationToken);
            }
            catch (SqliteException)
            {
                return false;
            }
        }

        var triggers = connection.CreateCommand();
        triggers.CommandText = """
            CREATE TRIGGER IF NOT EXISTS BookContentChunks_ai AFTER INSERT ON BookContentChunks BEGIN
                INSERT INTO BookContentFts(rowid, Content, ChapterTitle)
                VALUES (new.Id, new.Content, new.ChapterTitle);
            END;
            CREATE TRIGGER IF NOT EXISTS BookContentChunks_ad AFTER DELETE ON BookContentChunks BEGIN
                INSERT INTO BookContentFts(BookContentFts, rowid, Content, ChapterTitle)
                VALUES ('delete', old.Id, old.Content, old.ChapterTitle);
            END;
            CREATE TRIGGER IF NOT EXISTS BookContentChunks_au AFTER UPDATE ON BookContentChunks BEGIN
                INSERT INTO BookContentFts(BookContentFts, rowid, Content, ChapterTitle)
                VALUES ('delete', old.Id, old.Content, old.ChapterTitle);
                INSERT INTO BookContentFts(rowid, Content, ChapterTitle)
                VALUES (new.Id, new.Content, new.ChapterTitle);
            END;
            """;
        await triggers.ExecuteNonQueryAsync(cancellationToken);
        if (!existed)
        {
            var rebuild = connection.CreateCommand();
            rebuild.CommandText = "INSERT INTO BookContentFts(BookContentFts) VALUES('rebuild');";
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
        }
        return true;
    }

    private static async Task CreateFtsTableAsync(
        SqliteConnection connection,
        string tokenizer,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS BookContentFts USING fts5(
                Content,
                ChapterTitle,
                content='BookContentChunks',
                content_rowid='Id',
                tokenize='{tokenizer}'
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static BookContentChunk ReadChunk(SqliteDataReader reader, bool includeRank = false)
    {
        return new BookContentChunk(
            reader.GetInt64(0),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetString(10),
            includeRank && !reader.IsDBNull(11) ? reader.GetDouble(11) : 0);
    }

    private static readonly string[] ChineseStopPhrases =
    [
        "请根据", "请帮我", "这本书", "本书", "这一章", "本章", "当前章节", "当前",
        "如何", "怎么", "什么是", "为什么", "哪些", "是否", "请", "帮我", "一下",
        "总结", "概括", "解释", "分析", "介绍", "关于", "根据"
    ];

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[A-Za-z0-9_\-]{3,}")]
    private static partial Regex LatinWordRegex();

    [GeneratedRegex(@"[\u3400-\u9FFF]{2,}")]
    private static partial Regex ChineseSequenceRegex();
}
