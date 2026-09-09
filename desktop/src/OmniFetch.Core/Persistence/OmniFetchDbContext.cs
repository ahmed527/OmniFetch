using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OmniFetch.Core.Persistence.Entities;

namespace OmniFetch.Core.Persistence;

/// <summary>
/// Entity Framework Core database context for OmniFetch state persistence.
/// Configured for SQLite with Write-Ahead Logging (WAL) mode for high-concurrency read/write operations.
/// </summary>
public class OmniFetchDbContext : DbContext
{
    public DbSet<DownloadJobEntity> Jobs => Set<DownloadJobEntity>();
    public DbSet<DownloadSegmentEntity> Segments => Set<DownloadSegmentEntity>();

    public OmniFetchDbContext(DbContextOptions<OmniFetchDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Job Entity Configuration
        modelBuilder.Entity<DownloadJobEntity>(entity =>
        {
            entity.HasKey(j => j.Id);
            entity.Property(j => j.Url).IsRequired();
            entity.Property(j => j.DestinationFilePath).IsRequired();
            entity.Property(j => j.Status).IsRequired();
            entity.Property(j => j.CreatedAtUtc).IsRequired();

            entity.HasIndex(j => j.Status);
            entity.HasIndex(j => j.CreatedAtUtc);

            // One-to-many relationship with cascade deletion
            entity.HasMany(j => j.Segments)
                  .WithOne(s => s.Job)
                  .HasForeignKey(s => s.JobId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // Segment Entity Configuration
        modelBuilder.Entity<DownloadSegmentEntity>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.JobId).IsRequired();
            entity.Property(s => s.SegmentIndex).IsRequired();
            entity.Property(s => s.StartByte).IsRequired();
            entity.Property(s => s.EndByte).IsRequired();
            entity.Property(s => s.CurrentByte).IsRequired();

            entity.HasIndex(s => s.JobId);
            entity.HasIndex(s => new { s.JobId, s.SegmentIndex }).IsUnique();
        });
    }

    /// <summary>
    /// Configures high-performance SQLite pragmas (WAL mode, synchronous=NORMAL, 5s busy timeout).
    /// </summary>
    public static void ConfigureSqlitePragmas(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
        ";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the standard macOS application support file path for the OmniFetch database.
    /// Default: ~/Library/Application Support/OmniFetch/omnifetch.db
    /// </summary>
    public static string GetDefaultDatabasePath()
    {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appSupportDir = Path.Combine(userHome, "Library", "Application Support", "OmniFetch");
        if (!Directory.Exists(appSupportDir))
        {
            Directory.CreateDirectory(appSupportDir);
        }
        return Path.Combine(appSupportDir, "omnifetch.db");
    }

    /// <summary>
    /// Factory helper to build DbContextOptions for SQLite with WAL configuration.
    /// </summary>
    public static DbContextOptions<OmniFetchDbContext> CreateOptions(string? databasePath = null)
    {
        string path = databasePath ?? GetDefaultDatabasePath();
        var connectionStringBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        var optionsBuilder = new DbContextOptionsBuilder<OmniFetchDbContext>();
        optionsBuilder.UseSqlite(connectionStringBuilder.ToString());
        return optionsBuilder.Options;
    }
}
