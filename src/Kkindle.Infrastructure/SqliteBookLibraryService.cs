using System.Text;
using Microsoft.Data.Sqlite;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class SqliteBookLibraryService : IBookLibraryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3"
    };

    private readonly AppPaths _paths;
    private readonly IMetadataService _metadata;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);

    public SqliteBookLibraryService(AppPaths paths, IMetadataService metadata)
    {
        _paths = paths;
        _metadata = metadata;
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
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Books (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Authors TEXT NOT NULL,
                Series TEXT NULL,
                SeriesIndex REAL NULL,
                Description TEXT NULL,
                Tags TEXT NOT NULL DEFAULT '',
                CoverPath TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS BookFiles (
                Id TEXT PRIMARY KEY,
                BookId TEXT NOT NULL REFERENCES Books(Id) ON DELETE CASCADE,
                Format TEXT NOT NULL,
                RelativePath TEXT NOT NULL,
                Size INTEGER NOT NULL,
                Sha256 TEXT NOT NULL UNIQUE
            );
            CREATE INDEX IF NOT EXISTS IX_Books_TitleAuthors ON Books(Title, Authors);
            CREATE INDEX IF NOT EXISTS IX_BookFiles_BookId ON BookFiles(BookId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Book>> SearchAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Tags, CoverPath, CreatedAt, UpdatedAt
            FROM Books
            WHERE $query = '' OR Title LIKE $like OR Authors LIKE $like OR Tags LIKE $like OR Series LIKE $like
            ORDER BY UpdatedAt DESC, Title COLLATE NOCASE;
            """;
        var text = query?.Trim() ?? string.Empty;
        command.Parameters.AddWithValue("$query", text);
        command.Parameters.AddWithValue("$like", $"%{text}%");

        var books = new List<Book>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            books.Add(ReadBook(reader));
        }

        foreach (var book in books)
        {
            book.Files = await ReadFilesAsync(connection, book.Id, cancellationToken);
        }

        return books;
    }

    public async Task<ImportBatchResult> ImportAsync(IEnumerable<string> paths, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = ExpandInputFiles(paths).ToList();
        var result = new ImportBatchResult();
        var totalBytes = files.Sum(GetFileLengthSafe);
        long completedBytes = 0;

        foreach (var sourcePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(sourcePath);
                if (!file.Exists)
                    throw new FileNotFoundException("文件不存在", sourcePath);

                var hash = await Hashing.Sha256Async(sourcePath, cancellationToken);
                await using var connection = await OpenConnectionAsync(cancellationToken);
                var duplicate = await FindBookByHashAsync(connection, hash, cancellationToken);
                if (duplicate is not null)
                {
                    result.Items.Add(new ImportItemResult(sourcePath, true, "已存在，跳过重复文件", duplicate));
                    completedBytes += file.Length;
                    progress?.Report(new TransferProgress(completedBytes, totalBytes, $"已检查 {file.Name}"));
                    continue;
                }

                var metadata = await _metadata.ReadMetadataAsync(sourcePath, cancellationToken);
                var title = string.IsNullOrWhiteSpace(metadata.Title) ? Path.GetFileNameWithoutExtension(sourcePath) : metadata.Title.Trim();
                var authors = string.IsNullOrWhiteSpace(metadata.Authors) ? "未知作者" : metadata.Authors.Trim();
                var book = await FindBookByTitleAuthorsAsync(connection, title, authors, cancellationToken);
                var newBook = book is null;
                book ??= new Book
                {
                    Id = Guid.NewGuid(),
                    Title = title,
                    Authors = authors,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                book.Series ??= metadata.Series;
                book.SeriesIndex ??= metadata.SeriesIndex;
                book.Description ??= metadata.Description;
                book.UpdatedAt = DateTimeOffset.UtcNow;

                var bookDirectory = Path.Combine(_paths.Library, book.Id.ToString("N"));
                Directory.CreateDirectory(bookDirectory);
                var targetName = GetUniqueFileName(bookDirectory, Path.GetFileName(sourcePath));
                var targetPath = Path.Combine(bookDirectory, targetName);
                var temporaryPath = targetPath + ".part";
                try
                {
                    await CopyFileAsync(sourcePath, temporaryPath, file.Length, completedBytes, totalBytes, progress, cancellationToken);
                    File.Move(temporaryPath, targetPath, true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }

                if (newBook)
                {
                    await InsertBookAsync(connection, book, cancellationToken);
                }
                else
                {
                    await UpdateBookRowAsync(connection, book, cancellationToken);
                }

                var relativePath = Path.GetRelativePath(_paths.Data, targetPath);
                var bookFile = new BookFile
                {
                    Id = Guid.NewGuid(),
                    BookId = book.Id,
                    Format = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant(),
                    RelativePath = relativePath,
                    Size = file.Length,
                    Sha256 = hash
                };
                await InsertFileAsync(connection, bookFile, cancellationToken);
                book.Files.Add(bookFile);

                if (metadata.CoverBytes is { Length: > 0 } && string.IsNullOrWhiteSpace(book.CoverPath))
                {
                    var coverName = $"{book.Id:N}{NormalizeCoverExtension(metadata.CoverExtension)}";
                    var coverPath = Path.Combine(_paths.Covers, coverName);
                    await File.WriteAllBytesAsync(coverPath, metadata.CoverBytes, cancellationToken);
                    book.CoverPath = Path.GetRelativePath(_paths.Data, coverPath);
                    await UpdateBookRowAsync(connection, book, cancellationToken);
                }

                result.Items.Add(new ImportItemResult(sourcePath, true, newBook ? "已导入" : "已添加新格式", book));
                completedBytes += file.Length;
                progress?.Report(new TransferProgress(completedBytes, totalBytes, $"已导入 {file.Name}"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Items.Add(new ImportItemResult(sourcePath, false, ex.Message, null));
            }
        }

        return result;
    }

    public async Task<BookFile> AddFileToBookAsync(
        Guid bookId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("待添加的书籍文件不存在。", source);

        var format = BookFormatConversionPolicy.Normalize(Path.GetExtension(source));
        if (!BookFormatConversionPolicy.IsConvertibleFormat(format))
            throw new NotSupportedException("书库只允许添加 EPUB、AZW3 或 PDF 格式。 ");

        var fileInfo = new FileInfo(source);
        var hash = await Hashing.Sha256Async(source, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            if (!await BookExistsAsync(connection, bookId, cancellationToken))
                throw new InvalidOperationException("目标书籍已不存在，请刷新书库后重试。 ");

            if (await FindFileByHashAsync(connection, hash, cancellationToken) is not null)
                throw new InvalidOperationException("相同文件已经在书库中。 ");

            var bookDirectory = Path.Combine(_paths.Library, bookId.ToString("N"));
            Directory.CreateDirectory(bookDirectory);
            var targetName = GetUniqueFileName(bookDirectory, Path.GetFileName(source));
            var targetPath = Path.Combine(bookDirectory, targetName);
            var temporaryPath = targetPath + ".part";
            var targetCreated = false;
            var fileRowCreated = false;
            try
            {
                await CopyFileAsync(
                    source,
                    temporaryPath,
                    fileInfo.Length,
                    completed: 0,
                    total: fileInfo.Length,
                    progress: null,
                    cancellationToken);
                File.Move(temporaryPath, targetPath, true);
                targetCreated = true;

                var touch = connection.CreateCommand();
                touch.CommandText = "UPDATE Books SET UpdatedAt = $updatedAt WHERE Id = $bookId;";
                touch.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                touch.Parameters.AddWithValue("$bookId", bookId.ToString());
                await touch.ExecuteNonQueryAsync(cancellationToken);

                var bookFile = new BookFile
                {
                    Id = Guid.NewGuid(),
                    BookId = bookId,
                    Format = format,
                    RelativePath = Path.GetRelativePath(_paths.Data, targetPath),
                    Size = fileInfo.Length,
                    Sha256 = hash
                };
                await InsertFileAsync(connection, bookFile, cancellationToken);
                fileRowCreated = true;
                return bookFile;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    TryDeleteFile(temporaryPath);
                if (targetCreated && !fileRowCreated)
                    TryDeleteFile(targetPath);
            }
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task UpdateMetadataAsync(Book book, CancellationToken cancellationToken = default)
    {
        book.UpdatedAt = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await UpdateBookRowAsync(connection, book, cancellationToken);
    }

    public async Task DeleteFileAsync(
        Guid bookId,
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            string relativePath;
            string? coverPath;
            long fileCount;

            var lookup = connection.CreateCommand();
            lookup.CommandText = """
                SELECT f.RelativePath, b.CoverPath,
                       (SELECT COUNT(*) FROM BookFiles WHERE BookId = $bookId)
                FROM BookFiles f
                INNER JOIN Books b ON b.Id = f.BookId
                WHERE f.Id = $fileId AND f.BookId = $bookId
                LIMIT 1;
                """;
            lookup.Parameters.AddWithValue("$fileId", bookFileId.ToString());
            lookup.Parameters.AddWithValue("$bookId", bookId.ToString());
            await using (var reader = await lookup.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    throw new FileNotFoundException("指定书籍格式不存在。");

                relativePath = reader.GetString(0);
                coverPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                fileCount = reader.GetInt64(2);
            }

            var filePath = GetAbsoluteFilePath(new BookFile { RelativePath = relativePath });
            if (File.Exists(filePath))
                File.Delete(filePath);

            var deleteFile = connection.CreateCommand();
            deleteFile.CommandText = "DELETE FROM BookFiles WHERE Id = $fileId AND BookId = $bookId;";
            deleteFile.Parameters.AddWithValue("$fileId", bookFileId.ToString());
            deleteFile.Parameters.AddWithValue("$bookId", bookId.ToString());
            await deleteFile.ExecuteNonQueryAsync(cancellationToken);

            if (fileCount <= 1)
            {
                var deleteBook = connection.CreateCommand();
                deleteBook.CommandText = "DELETE FROM Books WHERE Id = $bookId;";
                deleteBook.Parameters.AddWithValue("$bookId", bookId.ToString());
                await deleteBook.ExecuteNonQueryAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(coverPath))
                {
                    var coverFile = Path.GetFullPath(Path.Combine(_paths.Data, coverPath));
                    if (File.Exists(coverFile)) File.Delete(coverFile);
                }
            }
            else
            {
                var updateBook = connection.CreateCommand();
                updateBook.CommandText = "UPDATE Books SET UpdatedAt = $updatedAt WHERE Id = $bookId;";
                updateBook.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                updateBook.Parameters.AddWithValue("$bookId", bookId.ToString());
                await updateBook.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally { _databaseGate.Release(); }
    }

    public async Task DeleteAsync(Guid bookId, CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var book = (await SearchAsync(bookId.ToString(), cancellationToken)).FirstOrDefault(x => x.Id == bookId);
            var directory = Path.Combine(_paths.Library, bookId.ToString("N"));
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            if (book?.CoverPath is not null)
            {
                var cover = Path.Combine(_paths.Data, book.CoverPath);
                if (File.Exists(cover)) File.Delete(cover);
            }

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Books WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", bookId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _databaseGate.Release(); }
    }

    public string GetAbsoluteFilePath(BookFile file)
    {
        var full = Path.GetFullPath(Path.Combine(_paths.Data, file.RelativePath));
        var dataRoot = Path.GetFullPath(_paths.Data + Path.DirectorySeparatorChar);
        if (!full.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("书籍路径不在应用数据目录内。");
        return full;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static IEnumerable<string> ExpandInputFiles(IEnumerable<string> paths)
    {
        foreach (var input in paths.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (File.Exists(input) && SupportedExtensions.Contains(Path.GetExtension(input)))
            {
                yield return Path.GetFullPath(input);
                continue;
            }

            if (Directory.Exists(input))
            {
                foreach (var file in Directory.EnumerateFiles(input, "*.*", SearchOption.AllDirectories)
                    .Where(x => SupportedExtensions.Contains(Path.GetExtension(x))))
                    yield return Path.GetFullPath(file);
            }
        }
    }

    private static long GetFileLengthSafe(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static string GetUniqueFileName(string directory, string originalName)
    {
        var safe = SanitizeFileName(originalName);
        var candidate = Path.Combine(directory, safe);
        if (!File.Exists(candidate)) return safe;
        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);
        for (var index = 2; ; index++)
        {
            var name = $"{stem} ({index}){extension}";
            if (!File.Exists(Path.Combine(directory, name))) return name;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(invalid.Contains(character) ? '_' : character);
        var result = builder.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(result) ? "book.bin" : result;
    }

    private static string NormalizeCoverExtension(string? extension)
    {
        var value = extension?.Trim().ToLowerInvariant() ?? ".jpg";
        return value is ".jpg" or ".jpeg" or ".png" or ".webp" ? value : ".jpg";
    }

    private async Task CopyFileAsync(string source, string target, long fileLength, long completed, long total, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress?.Report(new TransferProgress(completed + copied, total, $"正在复制 {Path.GetFileName(source)}"));
        }
        await output.FlushAsync(cancellationToken);
        if (copied != fileLength) throw new IOException("复制后的文件大小不一致。");
    }

    private static Book ReadBook(SqliteDataReader reader)
    {
        return new Book
        {
            Id = Guid.Parse(reader.GetString(0)),
            Title = reader.GetString(1),
            Authors = reader.GetString(2),
            Series = reader.IsDBNull(3) ? null : reader.GetString(3),
            SeriesIndex = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            Description = reader.IsDBNull(5) ? null : reader.GetString(5),
            Tags = reader.GetString(6),
            CoverPath = reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(8)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(9))
        };
    }

    private static async Task<List<BookFile>> ReadFilesAsync(SqliteConnection connection, Guid bookId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, BookId, Format, RelativePath, Size, Sha256 FROM BookFiles WHERE BookId = $bookId ORDER BY Format;";
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        var files = new List<BookFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new BookFile
            {
                Id = Guid.Parse(reader.GetString(0)),
                BookId = Guid.Parse(reader.GetString(1)),
                Format = reader.GetString(2),
                RelativePath = reader.GetString(3),
                Size = reader.GetInt64(4),
                Sha256 = reader.GetString(5)
            });
        }
        return files;
    }

    private static async Task<Book?> FindBookByHashAsync(SqliteConnection connection, string hash, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.Id, b.Title, b.Authors, b.Series, b.SeriesIndex, b.Description, b.Tags, b.CoverPath, b.CreatedAt, b.UpdatedAt
            FROM Books b INNER JOIN BookFiles f ON f.BookId = b.Id WHERE f.Sha256 = $hash LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBook(reader) : null;
    }

    private static async Task<Book?> FindBookByTitleAuthorsAsync(SqliteConnection connection, string title, string authors, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Authors, Series, SeriesIndex, Description, Tags, CoverPath, CreatedAt, UpdatedAt
            FROM Books WHERE lower(Title) = lower($title) AND lower(Authors) = lower($authors) LIMIT 1;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$authors", authors);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBook(reader) : null;
    }

    private static async Task<bool> BookExistsAsync(
        SqliteConnection connection,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Books WHERE Id = $bookId);";
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task<BookFile?> FindFileByHashAsync(
        SqliteConnection connection,
        string hash,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, BookId, Format, RelativePath, Size, Sha256 FROM BookFiles WHERE Sha256 = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new BookFile
        {
            Id = Guid.Parse(reader.GetString(0)),
            BookId = Guid.Parse(reader.GetString(1)),
            Format = reader.GetString(2),
            RelativePath = reader.GetString(3),
            Size = reader.GetInt64(4),
            Sha256 = reader.GetString(5)
        };
    }

    private static async Task InsertBookAsync(SqliteConnection connection, Book book, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Books (Id, Title, Authors, Series, SeriesIndex, Description, Tags, CoverPath, CreatedAt, UpdatedAt)
            VALUES ($id, $title, $authors, $series, $seriesIndex, $description, $tags, $coverPath, $createdAt, $updatedAt);
            """;
        AddBookParameters(command, book);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateBookRowAsync(SqliteConnection connection, Book book, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Books SET Title=$title, Authors=$authors, Series=$series, SeriesIndex=$seriesIndex, Description=$description,
                Tags=$tags, CoverPath=$coverPath, UpdatedAt=$updatedAt WHERE Id=$id;
            """;
        AddBookParameters(command, book);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddBookParameters(SqliteCommand command, Book book)
    {
        command.Parameters.AddWithValue("$id", book.Id.ToString());
        command.Parameters.AddWithValue("$title", book.Title);
        command.Parameters.AddWithValue("$authors", book.Authors);
        command.Parameters.AddWithValue("$series", (object?)book.Series ?? DBNull.Value);
        command.Parameters.AddWithValue("$seriesIndex", (object?)book.SeriesIndex ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)book.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$tags", book.Tags ?? string.Empty);
        command.Parameters.AddWithValue("$coverPath", (object?)book.CoverPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", book.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", book.UpdatedAt.ToString("O"));
    }

    private static async Task InsertFileAsync(SqliteConnection connection, BookFile file, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO BookFiles (Id, BookId, Format, RelativePath, Size, Sha256) VALUES ($id, $bookId, $format, $relativePath, $size, $sha256);";
        command.Parameters.AddWithValue("$id", file.Id.ToString());
        command.Parameters.AddWithValue("$bookId", file.BookId.ToString());
        command.Parameters.AddWithValue("$format", file.Format);
        command.Parameters.AddWithValue("$relativePath", file.RelativePath);
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$sha256", file.Sha256);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
