"""Run with Python 3.11+ from the repository root."""
from contextlib import closing
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools/catalog"))
from build import publish


class CatalogPublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        self.catalog = self.directory / "catalog.sqlite"
        self.world = self.directory / "world.sqlite"
        self.populate(self.world, self.directory)
        publish(self.catalog, self.populate, self.verify)
        self.original_catalog = self.catalog.read_bytes()
        self.original_world = self.world.read_bytes()

    @staticmethod
    def populate(candidate, work):
        with closing(sqlite3.connect(candidate)) as db, db:
            db.execute("CREATE TABLE sample(value INTEGER PRIMARY KEY)")
            db.execute("INSERT INTO sample VALUES(3662168)")

    @staticmethod
    def verify(candidate):
        with closing(sqlite3.connect(candidate.as_uri() + "?mode=ro", uri=True)) as db:
            if db.execute("SELECT value FROM sample").fetchone() != (3662168,):
                raise ValueError("Unexpected sample")

    def test_repeat(self):
        publish(self.catalog, self.populate, self.verify)
        self.verify(self.catalog)
        self.assertEqual(self.original_world, self.world.read_bytes())

    def test_interrupted_import(self):
        def interrupt(candidate, work):
            self.populate(candidate, work)
            raise KeyboardInterrupt()

        with self.assertRaises(KeyboardInterrupt):
            publish(self.catalog, interrupt, self.verify)
        self.assertEqual(self.original_catalog, self.catalog.read_bytes())
        self.assertEqual(self.original_world, self.world.read_bytes())

    def test_failed_validation(self):
        with self.assertRaises(sqlite3.DatabaseError):
            publish(self.catalog, lambda candidate, work: candidate.write_bytes(b"invalid candidate"), self.verify)
        self.assertEqual(self.original_catalog, self.catalog.read_bytes())
        self.assertEqual(self.original_world, self.world.read_bytes())


if __name__ == "__main__":
    unittest.main()
