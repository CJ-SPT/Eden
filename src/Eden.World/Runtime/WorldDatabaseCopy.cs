using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Eden.World.Runtime;

internal static class WorldDatabaseCopy
{
    // A page batch bounds work while persistence is held. SQLite restarts a backup
    // after another connection writes the source; callers must capture residents
    // immediately before the successful final batch, under the same owner.
    internal const int PagesPerStep = 256;

    internal sealed class Incremental : IDisposable
    {
        private readonly sqlite3_backup _backup;
        private readonly SqliteConnection _destination;

        internal Incremental(SqliteConnection source, SqliteConnection destination)
        {
            _destination = destination;
            _backup = raw.sqlite3_backup_init(destination.Handle!, "main", source.Handle!, "main");
            if (_backup.IsInvalid)
            {
                SqliteException.ThrowExceptionForRC(raw.sqlite3_errcode(destination.Handle!), destination.Handle);
            }
        }

        internal int Remaining => raw.sqlite3_backup_remaining(_backup);

        internal bool Step()
        {
            int result = raw.sqlite3_backup_step(_backup, PagesPerStep);
            if (result == raw.SQLITE_DONE)
            {
                return true;
            }
            SqliteException.ThrowExceptionForRC(result, _destination.Handle);
            return false;
        }

        public void Dispose() => _backup.Dispose();
    }

    internal static void Copy(SqliteConnection source, SqliteConnection destination)
    {
        // Serialize the relational image while persistence is held. Unlike backup,
        // this copies pages into one native buffer without rebuilding a page cache.
        // The destination owns that detached buffer and may grow during projection.
        nint image = raw.sqlite3_serialize(source.Handle!, "main", out long bytes, 0);
        if (image == 0)
        {
            throw new OutOfMemoryException("Unable to allocate the world checkpoint image.");
        }
        int result;
        try
        {
            result = raw.sqlite3_deserialize(
                destination.Handle!,
                "main",
                image,
                bytes,
                bytes,
                raw.SQLITE_DESERIALIZE_FREEONCLOSE | raw.SQLITE_DESERIALIZE_RESIZEABLE
            );
        }
        catch
        {
            raw.sqlite3_free(image);
            throw;
        }
        // FREEONCLOSE transfers ownership even on an SQLite error: deserialize
        // frees its input before returning failure. Do not free it a second time.
        SqliteException.ThrowExceptionForRC(result, destination.Handle);
    }
}
