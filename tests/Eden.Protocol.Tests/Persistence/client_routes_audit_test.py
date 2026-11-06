"""Focused RPC audit regressions using inert instruction fixtures."""
import importlib.util
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("client_routes", ROOT / "tools/Audit-ClientRoutes.py")
audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit)


def code(name, operations):
    instructions = [SimpleNamespace(opname=op, argval=value, arg=value if isinstance(value, int) else None, offset=i * 3)
                    for i, (op, value) in enumerate(operations)]
    return SimpleNamespace(co_name=name, instructions=instructions)


def remote(service):
    return [("LOAD_GLOBAL", "sm"), ("LOAD_ATTR", "RemoteSvc"), ("LOAD_CONST", service), ("CALL_FUNCTION", 1)]


class ClientRoutesAuditTests(unittest.TestCase):
    def inspector(self):
        return audit.Inspector(lambda value, _: value.instructions, None)

    def test_remote_alias_and_keyword_call_preserve_receiver(self):
        value = code("Read", remote("mailMgr") + [
            ("STORE_FAST", "mail"), ("LOAD_FAST", "mail"), ("LOAD_ATTR", "GetBody"),
            ("LOAD_CONST", 12), ("LOAD_CONST", "cache"), ("LOAD_CONST", 1), ("CALL_FUNCTION", 257)])
        inspector = self.inspector()
        inspector.scan([("fixture.pyj", [("Read", value)])])
        self.assertEqual({("mailMgr", "GetBody")}, set(inspector.routes))

    def test_bound_skill_reference_is_distinct_from_manager(self):
        value = code("Read", remote("skillMgr2") + [
            ("LOAD_ATTR", "GetMySkillHandler"), ("CALL_FUNCTION", 0),
            ("LOAD_ATTR", "GetSkills"), ("CALL_FUNCTION", 0)])
        inspector = self.inspector()
        inspector.scan([("fixture.pyj", [("Read", value)])])
        self.assertEqual({("skillMgr2", "GetMySkillHandler"), ("@skills", "GetSkills")}, set(inspector.routes))

    def test_dynamic_names_are_retained_without_inventing_routes(self):
        value = code("Read", [("LOAD_GLOBAL", "sm"), ("LOAD_ATTR", "RemoteSvc"),
                              ("LOAD_FAST", "service_name"), ("CALL_FUNCTION", 1)])
        inspector = self.inspector()
        inspector.scan([("fixture.pyj", [("Read", value)])])
        self.assertEqual({}, inspector.routes)
        self.assertEqual("dynamic service name", next(iter(inspector.unresolved))[-1])

    def test_local_services_are_not_server_routes(self):
        value = code("Read", [("LOAD_GLOBAL", "sm"), ("LOAD_ATTR", "GetService"), ("LOAD_CONST", "mail"),
                              ("CALL_FUNCTION", 1), ("LOAD_ATTR", "GetBody"), ("CALL_FUNCTION", 0)])
        inspector = self.inspector()
        inspector.scan([("fixture.pyj", [("Read", value)])])
        self.assertEqual({}, inspector.routes)

    def test_remote_callback_reference_is_included_without_direct_invocation(self):
        value = code("Read", [("LOAD_GLOBAL", "uthread"), ("LOAD_ATTR", "new")] + remote("mailMgr") + [
            ("LOAD_ATTR", "SendMail"), ("LOAD_CONST", 12), ("CALL_FUNCTION", 2)])
        inspector = self.inspector()
        inspector.scan([("fixture.pyj", [("Read", value)])])
        self.assertEqual({("mailMgr", "SendMail")}, set(inspector.routes))

    def test_remote_private_state_and_wrapper_data_are_not_method_references(self):
        inspector = self.inspector()
        for name in ("_Moniker__bindParams", "moniker"):
            value = code("Read", remote("invbroker") + [("LOAD_ATTR", name), ("POP_TOP", None)])
            inspector.inspect("fixture.pyj", "Read", value, True)
        self.assertEqual({}, inspector.routes)

    def test_cspa_string_dispatch_traces_mail_send(self):
        value = code("Send", [("LOAD_GLOBAL", "eveMisc"), ("LOAD_ATTR", "CSPAChargedAction"),
                              ("LOAD_CONST", "CSPAMailCheck")] + remote("mailMgr") + [
                              ("LOAD_CONST", "SendMail"), ("LOAD_CONST", "body"), ("CALL_FUNCTION", 4)])
        inspector = self.inspector()
        inspector.scan([("fixture.pyj", [("Send", value)])])
        self.assertEqual({("mailMgr", "SendMail")}, set(inspector.routes))

    def test_self_remote_field_flows_between_methods_but_not_classes(self):
        setup = code("Run", remote("mailMgr") + [("LOAD_FAST", "self"), ("STORE_ATTR", "manager")])
        read = code("Read", [("LOAD_FAST", "self"), ("LOAD_ATTR", "manager"),
                             ("LOAD_ATTR", "GetBody"), ("CALL_FUNCTION", 0)])
        other = code("ReadOther", [("LOAD_FAST", "self"), ("LOAD_ATTR", "manager"),
                                   ("LOAD_ATTR", "Delete"), ("CALL_FUNCTION", 0)])
        inspector = self.inspector()
        inspector.scan([("fixture.pyj", [("Mail.Run", setup), ("Mail.Read", read), ("Other.ReadOther", other)])])
        self.assertEqual({("mailMgr", "GetBody")}, set(inspector.routes))

    def test_local_service_return_summary_connects_another_module(self):
        owner = code("Mail", [])
        owner.co_consts = ("svc.mail",)
        factory = code("Manager", remote("mailMgr") + [("RETURN_VALUE", None)])
        caller = code("Read", [("LOAD_GLOBAL", "sm"), ("LOAD_ATTR", "GetService"),
                               ("LOAD_CONST", "mail"), ("CALL_FUNCTION", 1),
                               ("LOAD_ATTR", "Manager"), ("CALL_FUNCTION", 0),
                               ("LOAD_ATTR", "GetBody"), ("CALL_FUNCTION", 0)])
        inspector = self.inspector()
        inspector.scan([("service.pyj", [("<module>.Mail", owner), ("<module>.Mail.Manager", factory)]),
                        ("consumer.pyj", [("Read", caller)])])
        self.assertEqual({("mailMgr", "GetBody")}, set(inspector.routes))

    def test_inventory_cache_methods_are_local_and_missing_methods_forward(self):
        inspector = self.inspector()
        inspector.inventory_methods = {"GetItem"}
        for method in ("GetItem", "MultiAdd"):
            value = code("Read", [("LOAD_GLOBAL", "sm"), ("LOAD_ATTR", "GetService"),
                                   ("LOAD_CONST", "invCache"), ("CALL_FUNCTION", 1),
                                   ("LOAD_ATTR", "GetInventory"), ("CALL_FUNCTION", 0),
                                   ("LOAD_ATTR", method), ("CALL_FUNCTION", 0)])
            inspector.inspect("fixture.pyj", "Read", value, True)
        self.assertEqual({("@inventory", "MultiAdd")}, set(inspector.routes))

    def test_server_tuple_guards_and_compatibility_are_candidates_only(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            folder = root / "src/Eden.Protocol/Docking"
            folder.mkdir(parents=True)
            (folder / "SampleHandler.cs").write_text('''
                void Call(string service, string method) {
                    // ("absent", "Ghost")
                    if (service != "mailMgr" || method is not ("Send" or "GetBody")) return;
                    var reply = (service, method) switch { ("map", "Read" or "List") => 0 };
                }
            ''', encoding="utf-8")
            candidates, exact = audit.server_candidates(root)
            self.assertIn(("map", "List"), exact)
            self.assertEqual("handler-candidate", audit.classify("mailMgr", "Send", candidates, exact)[0])
            self.assertEqual("no-handler-found", audit.classify("absent", "Ghost", candidates, exact)[0])
            self.assertEqual("no-handler-found", audit.classify("mailMgr", "Delete", candidates, exact)[0])
            compatibility = "src/Eden.Protocol/Docking/DockedCompatibilityHandler.cs"
            self.assertEqual(("compatibility-review", [compatibility]),
                             audit.classify("map", "Empty", {("map", "Empty"): {compatibility}}, {}))


if __name__ == "__main__":
    unittest.main()
