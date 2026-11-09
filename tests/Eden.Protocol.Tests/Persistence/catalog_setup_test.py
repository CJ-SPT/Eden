"""Run with Python 3.11+ from the repository root."""
from contextlib import closing
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools/catalog"))
from build import publish
from generate_models import Emitter, Shape, generate
from runtime_policy import scan_code, violations


class CatalogAccessPolicyTests(unittest.TestCase):
    def test_features_use_shared_catalog_boundary(self):
        self.assertEqual([], violations())

    def test_direct_reads_are_rejected_and_explicit_boundaries_remain_available(self):
        path = "src/Eden.World/Inventory/Example.cs"
        for source in (
            "catalog.Set<ItemType>().Find(1)",
            "using T = Eden.Data.Catalog.ItemType; catalog.Set<T>().Find(1)",
            "CatalogDbContext source = new(path); source.Database.OpenConnection();",
            "using var source = new CatalogDbContext(path); CatalogData.Client.Types.Get(source, 1);",
            "CatalogDbContext source = new(path); CatalogData.Client.Types\n.Query(source);",
            "SourceRecord raw;",
            "catalog.FindSource(dataset, key);",
            "new SqliteConnection(catalogPath);",
        ):
            self.assertTrue(scan_code(path, source), source)
            self.assertEqual([], scan_code("tests/Fixtures/Example.cs", source))
        self.assertEqual([], scan_code(path, '// catalog.Set<ItemType>()\nvar text = "SourceRecord";'))
        self.assertTrue(scan_code("src/Eden.Server/Launcher/LauncherStore.cs", "SourceRecord raw;"))
        self.assertEqual([], scan_code("src/Eden.World/Skills/SkillCatalogProjection.cs", "new SqliteConnection(path);"))
        self.assertTrue(scan_code("src/Eden.World/Skills/OrdinarySkillReader.cs", "new SqliteConnection(path);"))


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


class CatalogModelGenerationTests(unittest.TestCase):
    def test_map_promotion_retains_previous_field_types(self):
        shape = Shape()
        shape.observe({"first": {"value": 7}})
        shape.observe({"123": {"value": "seven"}})
        emitter = Emitter()
        emitter.type(shape, "Sample")
        declarations = "\n".join(emitter.definitions)
        self.assertNotIn("JsonElement", declarations)
        self.assertIn("JsonTokenType.Number", declarations)
        self.assertIn("JsonTokenType.String", declarations)
        self.assertIn("long Value", declarations)
        self.assertIn("string Value", declarations)

    def test_mixed_numbers_preserve_large_integers_in_both_orders(self):
        for observations in ([2**53 + 1, 0.25], [0.25, 2**53 + 1]):
            shape = Shape()
            for value in observations:
                shape.observe(value)
            emitter = Emitter()
            emitter.type(shape, "ExactNumber")
            declarations = "\n".join(emitter.definitions)
            self.assertIn("long Value", declarations)
            self.assertIn("double Value", declarations)
            self.assertIn("when reader.TryGetInt64(out _)", declarations)
            self.assertEqual(shape.schema(), Shape.from_schema(shape.schema()).schema())

    def test_payload_models_are_generated_into_data_project(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            database = root / "catalog.sqlite"
            registry = root / "registry.g.cs"
            models = root / "models.g.cs"
            with closing(sqlite3.connect(database)) as db, db:
                db.execute("CREATE TABLE SourceDataset(Name TEXT PRIMARY KEY,Source TEXT,Sha256 TEXT,Rows INTEGER)")
                db.execute("CREATE TABLE SourceRecord(Dataset TEXT,Key TEXT,Data TEXT)")
                db.execute("INSERT INTO SourceDataset VALUES ('client/sample','pinned sample','sample-sha256',1)")
                db.execute("INSERT INTO SourceRecord VALUES ('client/sample','1','{\"quantity\":7}')")
            generate(database, registry, models)
            self.assertIn("namespace Eden.World.Catalog;", registry.read_text())
            self.assertIn("using Eden.Data.Catalog;", registry.read_text())
            self.assertNotIn("public sealed class", registry.read_text())
            self.assertIn("namespace Eden.Data.Catalog;", models.read_text())
            self.assertIn("public sealed class ClientSample", models.read_text())
            first = (registry.read_bytes(), models.read_bytes())
            generate(database, registry, models)
            self.assertEqual(first, (registry.read_bytes(), models.read_bytes()))


if __name__ == "__main__":
    unittest.main()
