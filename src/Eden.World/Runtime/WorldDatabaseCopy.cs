using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Eden.World.Runtime;

internal static class WorldDatabaseCopy
{
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
