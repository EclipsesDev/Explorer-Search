using ExplorerSearch.Core.Models;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Text;

namespace ExplorerSearch.Core.Services;

public sealed class FileSystemSearchService : IFileSearchService
{
    private const int FreePageVacuumThreshold = 2048;
    private const int IncrementalVacuumPages = 1024;
    private const int ProgressReportInterval = 1000;

    private readonly string _dbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExplorerSearch", "index.db");
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);

    public FileSystemSearchService()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();

        InitializeSchema(con);
        RunAutoMaintenance(con);
    }

    public Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SearchOptions();
        query = (query ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<FileSearchResult>>([]);
        }

        var roots = options.RootDirectories
            .Where(Directory.Exists)
            .Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (roots.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<FileSearchResult>>([]);
        }

        return Task.Run(() =>
        {
            using var con = new SqliteConnection($"Data Source={_dbPath}");
            con.Open();
            EnsureWatchers(roots);

            using var cmd = BuildSearchCommand(con, query, options, roots);
            using var reader = cmd.ExecuteReader();

            var comparison = options.MatchCase
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            var list = new List<FileSearchResult>(Math.Min(options.MaxResults, 256));
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = reader.GetString(0);
                if (!name.Contains(query, comparison))
                {
                    continue;
                }

                var fullPath = reader.GetString(1);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var unixSeconds = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
                long sizeBytes = 0;
                try
                {
                    var info = new FileInfo(fullPath);
                    if (info.Exists)
                    {
                        sizeBytes = info.Length;
                    }
                }
                catch
                {
                    // Ignore transient IO issues.
                }

                list.Add(new FileSearchResult
                {
                    Name = name,
                    FullPath = fullPath,
                    SizeBytes = sizeBytes,
                    LastWriteTime = unixSeconds > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                        : DateTimeOffset.MinValue
                });

                if (list.Count >= options.MaxResults)
                {
                    break;
                }
            }

            return (IReadOnlyList<FileSearchResult>)list;
        }, cancellationToken);
    }

    public Task WarmupIndexAsync(IEnumerable<string> roots, CancellationToken cancellationToken = default)
    {
        return WarmupIndexAsync(roots, progress: null, cancellationToken);
    }

    public Task WarmupIndexAsync(
        IEnumerable<string> roots,
        IProgress<IndexBuildProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var validRoots = roots
            .Where(Directory.Exists)
            .Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (validRoots.Length == 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            using var con = new SqliteConnection($"Data Source={_dbPath}");
            con.Open();
            EnsureRootsIndexed(con, validRoots, progress, cancellationToken);
            EnsureWatchers(validRoots);
        }, cancellationToken);
    }

    public Task RebuildIndexAsync(IEnumerable<string> roots, CancellationToken cancellationToken = default)
    {
        var validRoots = roots
            .Where(Directory.Exists)
            .Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (validRoots.Length == 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            using var con = new SqliteConnection($"Data Source={_dbPath}");
            con.Open();

            foreach (var root in validRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexRoot(con, root, cancellationToken);
            }

            RunAutoMaintenance(con);
            EnsureWatchers(validRoots);
        }, cancellationToken);
    }

    private static void InitializeSchema(SqliteConnection con)
    {
        using (var pragmaCmd = con.CreateCommand())
        {
            pragmaCmd.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                PRAGMA auto_vacuum=INCREMENTAL;
                PRAGMA cache_size=-8192;
                """;
            pragmaCmd.ExecuteNonQuery();
        }

        using (var rootCmd = con.CreateCommand())
        {
            rootCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS IndexedRoots(
                    RootPath TEXT PRIMARY KEY,
                    LastIndexedUtc TEXT NOT NULL
                );
                """;
            rootCmd.ExecuteNonQuery();
        }

        if (!TableExists(con, "Files"))
        {
            CreateCompactFilesTable(con);
            return;
        }

        if (RequiresCompactMigration(con))
        {
            MigrateToCompactSchema(con);
        }

        using var ensureIdx = con.CreateCommand();
        ensureIdx.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_Files_Name ON Files(Name);
            DROP INDEX IF EXISTS IX_Files_ParentPath;
            """;
        ensureIdx.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection con, string tableName)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool RequiresCompactMigration(SqliteConnection con)
    {
        var columns = GetTableColumns(con, "Files");
        return !(columns.Count == 3
            && columns.Contains("FullPath")
            && columns.Contains("Name")
            && columns.Contains("LastWriteUnixSeconds"));
    }

    private static HashSet<string> GetTableColumns(SqliteConnection con, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static void CreateCompactFilesTable(SqliteConnection con)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Files(
                FullPath TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                LastWriteUnixSeconds INTEGER NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS IX_Files_Name ON Files(Name);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void MigrateToCompactSchema(SqliteConnection con)
    {
        var columns = GetTableColumns(con, "Files");

        var insertSelect = columns.Contains("LastWriteUnixSeconds")
            ? "SELECT FullPath, Name, COALESCE(LastWriteUnixSeconds, 0) FROM Files"
            : columns.Contains("LastWriteUtc")
                ? "SELECT FullPath, Name, COALESCE(CAST(strftime('%s', LastWriteUtc) AS INTEGER), 0) FROM Files"
                : "SELECT FullPath, Name, 0 FROM Files";

        using var tx = con.BeginTransaction();

        using (var createCmd = con.CreateCommand())
        {
            createCmd.Transaction = tx;
            createCmd.CommandText = """
                DROP TABLE IF EXISTS Files_New;
                CREATE TABLE Files_New(
                    FullPath TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    LastWriteUnixSeconds INTEGER NOT NULL
                ) WITHOUT ROWID;
                """;
            createCmd.ExecuteNonQuery();
        }

        using (var copyCmd = con.CreateCommand())
        {
            copyCmd.Transaction = tx;
            copyCmd.CommandText = $"INSERT OR REPLACE INTO Files_New(FullPath, Name, LastWriteUnixSeconds) {insertSelect};";
            copyCmd.ExecuteNonQuery();
        }

        using (var swapCmd = con.CreateCommand())
        {
            swapCmd.Transaction = tx;
            swapCmd.CommandText = """
                DROP TABLE Files;
                ALTER TABLE Files_New RENAME TO Files;
                CREATE INDEX IF NOT EXISTS IX_Files_Name ON Files(Name);
                DROP INDEX IF EXISTS IX_Files_ParentPath;
                """;
            swapCmd.ExecuteNonQuery();
        }

        tx.Commit();

        using var vacuum = con.CreateCommand();
        vacuum.CommandText = "VACUUM;";
        vacuum.ExecuteNonQuery();
    }

    private static void RunAutoMaintenance(SqliteConnection con)
    {
        using (var optimize = con.CreateCommand())
        {
            optimize.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(PASSIVE);";
            optimize.ExecuteNonQuery();
        }

        long freePages;
        using (var freeListCmd = con.CreateCommand())
        {
            freeListCmd.CommandText = "PRAGMA freelist_count;";
            freePages = (long)(freeListCmd.ExecuteScalar() ?? 0L);
        }

        if (freePages < FreePageVacuumThreshold)
        {
            return;
        }

        using var vacuumCmd = con.CreateCommand();
        vacuumCmd.CommandText = $"PRAGMA incremental_vacuum({IncrementalVacuumPages});";
        vacuumCmd.ExecuteNonQuery();
    }

    private void EnsureWatchers(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            if (_watchers.ContainsKey(root))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*"
            };

            watcher.Created += OnWatcherCreatedOrChanged;
            watcher.Changed += OnWatcherCreatedOrChanged;
            watcher.Deleted += OnWatcherDeleted;
            watcher.Renamed += OnWatcherRenamed;
            watcher.Error += (_, _) => { };
            watcher.EnableRaisingEvents = true;

            if (!_watchers.TryAdd(root, watcher))
            {
                watcher.Dispose();
            }
        }
    }

    private void OnWatcherCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        if (!File.Exists(e.FullPath))
        {
            return;
        }

        try
        {
            UpsertPath(e.FullPath);
        }
        catch
        {
            // Ignore watcher races.
        }
    }

    private void OnWatcherDeleted(object sender, FileSystemEventArgs e)
    {
        try
        {
            DeletePath(e.FullPath);
        }
        catch
        {
            // Ignore watcher races.
        }
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            DeletePath(e.OldFullPath);
            if (File.Exists(e.FullPath))
            {
                UpsertPath(e.FullPath);
            }
        }
        catch
        {
            // Ignore watcher races.
        }
    }

    private void UpsertPath(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            return;
        }

        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO Files(FullPath, Name, LastWriteUnixSeconds)
            VALUES($fullPath, $name, $lastWriteUnixSeconds);
            """;
        cmd.Parameters.AddWithValue("$fullPath", fullPath);
        cmd.Parameters.AddWithValue("$name", info.Name);
        cmd.Parameters.AddWithValue("$lastWriteUnixSeconds", new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    private void DeletePath(string fullPath)
    {
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM Files WHERE FullPath = $fullPath;";
        cmd.Parameters.AddWithValue("$fullPath", fullPath);
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand BuildSearchCommand(SqliteConnection con, string query, SearchOptions options, IReadOnlyList<string> roots)
    {
        var sql = new StringBuilder();
        sql.AppendLine("SELECT Name, FullPath, LastWriteUnixSeconds");
        sql.AppendLine("FROM Files");
        sql.AppendLine("WHERE Name LIKE $q ESCAPE '\\'");
        sql.AppendLine("  AND (");

        for (var i = 0; i < roots.Count; i++)
        {
            if (i > 0)
            {
                sql.AppendLine("    OR");
            }

            if (options.IncludeSubdirectories)
            {
                sql.Append($"    instr(FullPath, $rp{i}) = 1");
            }
            else
            {
                sql.Append($"    (instr(FullPath, $rp{i}) = 1 AND instr(substr(FullPath, length($rp{i}) + 1), '\\\\') = 0)");
            }

            sql.AppendLine();
        }

        sql.AppendLine("  )");
        sql.AppendLine("LIMIT $max;");

        var cmd = con.CreateCommand();
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$q", $"%{EscapeLike(query)}%");
        cmd.Parameters.AddWithValue("$max", options.MatchCase ? Math.Max(options.MaxResults * 5, options.MaxResults) : options.MaxResults);

        for (var i = 0; i < roots.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$rp{i}", roots[i] + "\\");
        }

        return cmd;
    }

    private static void EnsureRootsIndexed(
        SqliteConnection con,
        IReadOnlyList<string> roots,
        IProgress<IndexBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var check = con.CreateCommand();
            check.CommandText = "SELECT 1 FROM IndexedRoots WHERE RootPath = $root LIMIT 1;";
            check.Parameters.AddWithValue("$root", root);

            if (check.ExecuteScalar() is null)
            {
                IndexRoot(con, root, progress, cancellationToken);
            }
        }
    }

    private static void EnsureRootsIndexed(SqliteConnection con, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        EnsureRootsIndexed(con, roots, progress: null, cancellationToken);
    }

    private static void IndexRoot(
        SqliteConnection con,
        string root,
        IProgress<IndexBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totalFiles = 0;

        progress?.Report(new IndexBuildProgress
        {
            RootPath = root,
            CurrentFileName = "Counting files..."
        });

        if (progress is not null)
        {
            try
            {
                var countEnumerationOptions = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Temporary
                };

                foreach (var _ in Directory.EnumerateFiles(root, "*", countEnumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalFiles++;
                }
            }
            catch
            {
                totalFiles = 0;
            }
        }

        using var tx = con.BeginTransaction();

        using (var deleteCmd = con.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM Files WHERE instr(FullPath, $rootPrefix) = 1;";
            deleteCmd.Parameters.AddWithValue("$rootPrefix", root + "\\");
            deleteCmd.ExecuteNonQuery();
        }

        using var insertCmd = con.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = """
            INSERT OR REPLACE INTO Files(FullPath, Name, LastWriteUnixSeconds)
            VALUES($fullPath, $name, $lastWriteUnixSeconds);
            """;

        var pFullPath = insertCmd.Parameters.Add("$fullPath", SqliteType.Text);
        var pName = insertCmd.Parameters.Add("$name", SqliteType.Text);
        var pLastWriteUnixSeconds = insertCmd.Parameters.Add("$lastWriteUnixSeconds", SqliteType.Integer);
        insertCmd.Prepare();

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.Temporary
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", enumerationOptions);
        }
        catch
        {
            tx.Rollback();
            return;
        }

        var processedFiles = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                pFullPath.Value = filePath;
                pName.Value = Path.GetFileName(filePath);
                pLastWriteUnixSeconds.Value = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath)).ToUnixTimeSeconds();
                insertCmd.ExecuteNonQuery();
                processedFiles++;

                if (progress is not null && (processedFiles == 1 || processedFiles % ProgressReportInterval == 0))
                {
                    var totalForDisplay = totalFiles > 0 ? totalFiles : processedFiles;
                    TimeSpan? estimatedRemaining = null;
                    if (totalFiles > 0 && processedFiles > 0)
                    {
                        var remaining = Math.Max(0, totalFiles - processedFiles);
                        var secondsPerFile = stopwatch.Elapsed.TotalSeconds / processedFiles;
                        estimatedRemaining = TimeSpan.FromSeconds(remaining * secondsPerFile);
                    }

                    progress.Report(new IndexBuildProgress
                    {
                        RootPath = root,
                        ProcessedFiles = processedFiles,
                        TotalFiles = totalForDisplay,
                        EstimatedRemaining = estimatedRemaining,
                        CurrentFileName = Path.GetFileName(filePath)
                    });
                }
            }
            catch
            {
                // Ignore files that disappear or become inaccessible while indexing.
            }
        }

        progress?.Report(new IndexBuildProgress
        {
            RootPath = root,
            ProcessedFiles = processedFiles,
            TotalFiles = processedFiles,
            EstimatedRemaining = null,
            CurrentFileName = "Finalizing..."
        });

        using (var upsertRoot = con.CreateCommand())
        {
            upsertRoot.Transaction = tx;
            upsertRoot.CommandText = """
                INSERT INTO IndexedRoots(RootPath, LastIndexedUtc)
                VALUES($root, $lastIndexedUtc)
                ON CONFLICT(RootPath) DO UPDATE SET LastIndexedUtc = excluded.LastIndexedUtc;
                """;
            upsertRoot.Parameters.AddWithValue("$root", root);
            upsertRoot.Parameters.AddWithValue("$lastIndexedUtc", DateTime.UtcNow.ToString("O"));
            upsertRoot.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void IndexRoot(SqliteConnection con, string root, CancellationToken cancellationToken)
    {
        IndexRoot(con, root, progress: null, cancellationToken);
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return root;
        }

        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string EscapeLike(string s) =>
        s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}