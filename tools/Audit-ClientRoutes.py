"""Inspect Python 2 client bytecode as data; never import or execute client code.

Requires xdis 6.3.0 (install separately or supply --python-libs).
The result is a static RPC backlog, not a claim of complete gameplay support.
"""
import argparse
from collections import Counter, defaultdict
import csv
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import sys
import zipfile
import zlib


PINNED_SHA256 = "37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368"
UNKNOWN = ("unknown", "")
SELECTORS = {"RemoteSvc", "ProxySvc", "StartServiceAndWaitForRemote", "ConnectToRemoteService"}
LOCAL_SELECTORS = {"GetService", "StartService", "GetServiceIfRunning"}
MONIKER_HELPERS = {
    "SetSessionCheck", "Bind", "Unbind", "GetSelfLocal", "GetSelfLocalWithLog",
    "ToURLString", "FromURLString", "MonikeredCall", "QuickResolve", "Resolve", "Unresolve", "GetNodeID",
}
# Verified against the pinned michelle.GetRemotePark and invCache's dynamic
# __getattr__/container wrappers. These boundaries cannot be inferred from
# ordinary return instructions alone. The inventory alias is a union of
# possible containers, so matching handlers still require location review.
LOCAL_REMOTE_BRIDGES = {
    ("michelle", "GetRemotePark"): "beyonce",
    ("invCache", "GetInventoryMgr"): "invbroker",
    ("invCache", "GetInventory"): ("inventory-wrapper", "@inventory"),
    ("invCache", "GetInventoryFromId"): ("inventory-wrapper", "@inventory"),
}
# These RPCs return another remote object, rather than an ordinary value.
RETURNED_REFERENCES = {
    ("skillMgr2", "GetMySkillHandler"): "@skills",
    ("invbroker", "GetInventory"): "@inventory",
    ("invbroker", "GetInventoryFromId"): "@inventory",
}
INVENTORY_ALIASES = {"@hangar", "@ship", "@pilot", "@globalInventory"}


def symbol(name):
    return ("symbol", name)


def attribute(value, name):
    return ("attribute", value, name)


def leaf(value):
    if value[0] == "attribute":
        return value[2]
    return value[1] if value[0] == "symbol" else ""


def walk(code, parent=""):
    path = parent + "." + code.co_name if parent else code.co_name
    yield path, code
    for child in code.co_consts:
        if hasattr(child, "co_code"):
            yield from walk(child, path)


def strings(pattern):
    return re.findall(r'"([^"\n]+)"', pattern)


def server_candidates(root):
    """Conservative source candidates, including guard dispatch and wire adapters.

    A cross-product within a dispatch file deliberately over-approximates matches.
    This prevents reporting an absent handler merely because it uses an if/guard
    instead of a tuple switch. Candidates always require semantic review.
    """
    candidates = defaultdict(set)
    exact = defaultdict(set)
    files = sorted((root / "src/Eden.Protocol").rglob("*.cs"))
    for file in files:
        text = file.read_text(encoding="utf-8-sig")
        if not re.search(r"\bstring service\b", text):
            continue
        path = file.relative_to(root).as_posix()
        # Remove comments before collecting dispatch literals.
        text = re.sub(r"//[^\n]*|/\*.*?\*/", "", text, flags=re.S)
        services = set()
        for match in re.finditer(r'\(\s*((?:"[^"\n]+"\s*(?:or\s*)?)+),\s*'
                                 r'((?:"[^"\n]+"\s*(?:or\s*)?)+)\)', text):
            for service in strings(match[1]):
                services.add(service)
                for method in strings(match[2]):
                    exact[service, method].add(path)
        for match in re.finditer(r'\bservice\s*(?:==|!=|is\s+(?:not\s+)?)\s*'
                                 r'\(?\s*((?:"[^"\n]+"\s*(?:or\s*)?)+)', text):
            services.update(strings(match[1]))
        for match in re.finditer(r'\bservice\s*(?:==|!=)\s*(\w+)', text):
            constant = re.search(r'const\s+string\s+' + re.escape(match[1]) + r'\s*=\s*"([^"\n]+)"', text)
            if constant:
                services.add(constant[1])
        # Use every identifier-like string for candidates only. This also covers
        # method enums, nested switches, negated guards and delegated parsers.
        methods = {s for s in strings(text) if re.fullmatch(r"[A-Za-z_][A-Za-z_0-9]*", s)}
        for service in services:
            for method in methods:
                candidates[service, method].add(path)
    return candidates, exact


class Inspector:
    def __init__(self, bytecode, opcodes):
        self.bytecode = bytecode
        self.opcodes = opcodes
        self.summaries = {}
        self.attributes = {}
        self.owners = {}
        self.service_summaries = {}
        self.local_services = set()
        self.inventory_methods = set()
        self.routes = defaultdict(set)
        self.unresolved = set()
        self.failures = []

    def inspect(self, module, path, code, collect):
        stack = []
        locals_ = {}
        returns = set()
        location = (module, path)

        def pop():
            return stack.pop() if stack else UNKNOWN

        def resolve(value):
            if value[0] == "attribute" and value[1] == symbol("self"):
                stored = self.attributes.get((module, path.rsplit(".", 1)[0], value[2]))
                if stored:
                    return stored
                if value[2] in self.local_services:
                    return ("local", value[2])
            return value

        def call(function, args, offset):
            name = leaf(function)
            literal = args[0][1] if args and args[0][0] == "constant" and isinstance(args[0][1], str) else None
            # eveMisc.CSPAChargedAction(label, remote, method, *args) invokes
            # getattr(remote, method); mail sending uses this string dispatcher.
            if name == "CSPAChargedAction" and len(args) >= 3 and args[1][0] == "remote":
                if args[2][0] == "constant" and isinstance(args[2][1], str):
                    if collect:
                        self.routes[args[1][1], args[2][1]].add((*location, offset))
                elif collect:
                    self.unresolved.add((*location, offset, args[1][1], "dynamic CSPA remote method"))
                return UNKNOWN
            if name in SELECTORS or name == "Moniker":
                if literal:
                    return ("remote", literal)
                if collect:
                    self.unresolved.add((*location, offset, name, "dynamic service name"))
                return UNKNOWN
            if name in LOCAL_SELECTORS and literal:
                return ("local", literal)
            if name == "getattr" and len(args) >= 2 and args[0][0] == "remote":
                if args[1][0] == "constant" and isinstance(args[1][1], str):
                    return attribute(args[0], args[1][1])
                if collect:
                    self.unresolved.add((*location, offset, args[0][1], "dynamic remote method"))
                return UNKNOWN
            if function[0] == "attribute":
                receiver = resolve(function[1])
                local_service = receiver[1] if receiver[0] == "local" else None
                if receiver == symbol("self"):
                    local_service = self.owners.get((module, path.rsplit(".", 1)[0]))
                bridge = LOCAL_REMOTE_BRIDGES.get((local_service, name))
                if bridge:
                    return bridge if isinstance(bridge, tuple) else ("remote", bridge)
                summary = self.service_summaries.get((local_service, name))
                if summary:
                    return summary
                if receiver[0] == "inventory-wrapper":
                    # Existing cache methods execute locally; their internal
                    # moniker RPCs are traced separately below. __getattr__ only
                    # forwards names that the wrapper does not implement.
                    if name in self.inventory_methods:
                        return UNKNOWN
                    receiver = ("remote", "@inventory")
                if receiver[0] == "remote":
                    service = receiver[1]
                    if name in MONIKER_HELPERS:
                        return receiver if name.startswith("GetSelfLocal") else UNKNOWN
                    if collect:
                        self.routes[service, name].add((*location, function[3] if len(function) > 3 else offset))
                    bound = RETURNED_REFERENCES.get((service, name))
                    return ("remote", bound) if bound else UNKNOWN
            # Within-module return summaries include self.GetRemoteThing().
            key = (module, name)
            if key in self.summaries:
                return self.summaries[key]
            # eveMoniker is a shared collection of remote-object factories.
            if function[0] == "attribute" and leaf(function[1]) == "eveMoniker":
                return self.summaries.get(("eve/common/script/net/eveMoniker.pyj", name), UNKNOWN)
            return UNKNOWN

        for instruction in self.bytecode(code, self.opcodes):
            op, arg = instruction.opname, instruction.argval
            if op == "LOAD_CONST":
                stack.append(("constant", arg) if isinstance(arg, (str, int, float, type(None))) else UNKNOWN)
            elif op in {"LOAD_FAST", "LOAD_NAME", "LOAD_GLOBAL", "LOAD_DEREF"}:
                stack.append(locals_.get(arg, symbol(arg)))
            elif op == "LOAD_ATTR":
                receiver = resolve(pop())
                if receiver[0] == "inventory-wrapper" and arg not in self.inventory_methods:
                    receiver = ("remote", "@inventory")
                method_reference = not arg.startswith("_") and (arg[0].isupper() or "_" in arg)
                if collect and receiver[0] == "remote" and arg not in MONIKER_HELPERS and method_reference:
                    # Record references too: uthread/session helpers often invoke
                    # a remote method supplied as a callback instead of emitting
                    # CALL_FUNCTION directly on that receiver here.
                    self.routes[receiver[1], arg].add((*location, instruction.offset))
                stack.append(resolve(("attribute", receiver, arg, instruction.offset)))
            elif op in {"STORE_FAST", "STORE_NAME", "STORE_DEREF"}:
                locals_[arg] = pop()
            elif op == "STORE_ATTR":
                receiver, value = pop(), pop()
                if receiver == symbol("self") and value[0] == "remote":
                    key = (module, path.rsplit(".", 1)[0], arg)
                    self.attributes[key] = value
            elif op.startswith("CALL_FUNCTION"):
                packed = instruction.arg or 0
                extra = int(op.endswith("VAR")) + int(op.endswith("KW"))
                if op.endswith("VAR_KW"):
                    extra = 2
                for _ in range(extra + 2 * (packed >> 8)):
                    pop()
                args = [pop() for _ in range(packed & 255)][::-1]
                stack.append(call(pop(), args, instruction.offset))
            elif op == "RETURN_VALUE":
                value = pop()
                if value[0] == "remote":
                    returns.add(value)
                stack.clear()
            elif op in {"BUILD_TUPLE", "BUILD_LIST", "BUILD_SET"}:
                for _ in range(instruction.arg or 0):
                    pop()
                stack.append(UNKNOWN)
            elif op == "BUILD_MAP":
                stack.append(UNKNOWN)
            elif op == "STORE_MAP":
                pop()
                pop()
            elif op == "DUP_TOP":
                stack.append(stack[-1] if stack else UNKNOWN)
            elif op == "ROT_TWO" and len(stack) >= 2:
                stack[-2:] = stack[-2:][::-1]
            elif op == "POP_TOP" or op.startswith("POP_JUMP"):
                pop()
            elif op in {"JUMP_FORWARD", "JUMP_ABSOLUTE", "SETUP_LOOP", "POP_BLOCK", "NOP", "EXTENDED_ARG"}:
                pass
            elif op.startswith("BINARY_") or op in {"COMPARE_OP", "INPLACE_ADD", "INPLACE_SUBTRACT"}:
                pop()
                pop()
                stack.append(UNKNOWN)
            elif op.startswith("UNARY_") or op == "GET_ITER":
                pop()
                stack.append(UNKNOWN)
            elif op == "UNPACK_SEQUENCE":
                pop()
                stack.extend([UNKNOWN] * (instruction.arg or 0))
            elif op == "FOR_ITER":
                stack.append(UNKNOWN)
            elif op == "MAKE_FUNCTION":
                pop()
                for _ in range(instruction.arg or 0):
                    pop()
                stack.append(UNKNOWN)
            elif op == "IMPORT_NAME":
                pop()
                pop()
                stack.append(symbol(arg))
            elif op == "IMPORT_FROM":
                stack.append(symbol(arg))
            else:
                # Unsupported stack operations cannot propagate invented values.
                stack.clear()
        return returns

    def scan(self, modules):
        for module, codes in modules:
            for path, code in codes:
                for value in getattr(code, "co_consts", ()):
                    if isinstance(value, str) and re.fullmatch(r"svc\.[A-Za-z_0-9]+", value):
                        self.owners[module, path] = value[4:]
                if module == "eve/client/script/environment/invCache.pyj":
                    owner = path.rsplit(".", 1)[0]
                    if owner in {"<module>.invCacheContainer", "<module>.inv5MinCacheContainer"}:
                        self.inventory_methods.add(code.co_name)
                        service = "@globalInventory" if owner.endswith("inv5MinCacheContainer") else "@inventory"
                        self.attributes[module, owner, "moniker"] = ("remote", service)
        self.local_services = set(self.owners.values())

        def relevant_names():
            # An expression can only acquire a known remote receiver through
            # one of these symbols. Skip decoding unrelated instructions while
            # still visiting every code object's names and constants.
            return (SELECTORS | LOCAL_SELECTORS | {"Moniker", "eveMoniker", "getattr"} | self.local_services
                    | {name for _, name in self.summaries} | {name for _, _, name in self.attributes}
                    | {name for _, name in LOCAL_REMOTE_BRIDGES})

        def relevant(code, names):
            return not hasattr(code, "co_names") or bool(names.intersection(code.co_names))
        # Resolve wrappers and self.remoteSvc assignments before collecting calls.
        # Only unambiguous remote return summaries are used.
        for _ in range(4):
            proposed = defaultdict(set)
            before = (dict(self.summaries), dict(self.attributes))
            names = relevant_names()
            for module, codes in modules:
                for path, code in codes:
                    if relevant(code, names):
                        proposed[module, code.co_name].update(self.inspect(module, path, code, False))
            self.summaries = {key: next(iter(values)) for key, values in proposed.items() if len(values) == 1}
            service_returns = defaultdict(set)
            for module, codes in modules:
                for path, code in codes:
                    owner = self.owners.get((module, path.rsplit(".", 1)[0]))
                    summary = self.summaries.get((module, code.co_name))
                    if owner and summary:
                        service_returns[owner, code.co_name].add(summary)
            self.service_summaries = {key: next(iter(values)) for key, values in service_returns.items() if len(values) == 1}
            if before == (self.summaries, self.attributes):
                break
        names = relevant_names()
        for module, codes in modules:
            for path, code in codes:
                try:
                    if relevant(code, names):
                        self.inspect(module, path, code, True)
                except Exception as error:
                    self.failures.append({"module": module, "function": path, "error": str(error)})


def classify(service, method, candidates, exact):
    aliases = INVENTORY_ALIASES if service == "@inventory" else {service}
    paths = set().union(*(candidates.get((alias, method), set()) for alias in aliases))
    exact_paths = set().union(*(exact.get((alias, method), set()) for alias in aliases))
    if not paths:
        return "no-handler-found", []
    if all(path.endswith(("/DockedCompatibilityHandler.cs", "/AccountCompatibilityHandler.cs")) for path in paths):
        return "compatibility-review", sorted(paths)
    return "handler-candidate", sorted(exact_paths or paths)


def write_results(output, root, archive, inspector, module_count, code_count):
    output.mkdir(parents=True, exist_ok=True)
    candidates, exact = server_candidates(root)
    routes = []
    for (service, method), sites in sorted(inspector.routes.items()):
        status, paths = classify(service, method, candidates, exact)
        routes.append({"service": service, "method": method, "status": status,
                       "server_candidates": paths,
                       "client_sites": [{"module": m, "function": f, "offset": o} for m, f, o in sorted(sites)]})
    counts = Counter(route["status"] for route in routes)
    revision = subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip()
    metadata = {"client_build": 3662168, "archive_sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                "eden_revision": revision, "modules_scanned": module_count, "code_objects_scanned": code_count,
                "route_counts": dict(sorted(counts.items())), "unresolved_selectors": len(inspector.unresolved),
                "failures": inspector.failures}
    (output / "routes.json").write_text(json.dumps({"metadata": metadata, "routes": routes}, indent=2) + "\n", encoding="utf-8")
    with (output / "routes.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["service", "method", "status", "client_call_sites", "server_candidates", "first_client_site"])
        for route in routes:
            first = route["client_sites"][0]
            writer.writerow([route["service"], route["method"], route["status"], len(route["client_sites"]),
                             "; ".join(route["server_candidates"]), f'{first["module"]}:{first["function"]}@{first["offset"]}'])
    with (output / "unresolved.csv").open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["module", "function", "offset", "selector_or_service", "reason"])
        writer.writerows(sorted(inspector.unresolved))
    missing = defaultdict(list)
    for route in routes:
        if route["status"] == "no-handler-found":
            missing[route["service"]].append(route["method"])
    lines = ["# Client RPC route inventory", "", f"Client build: **3662168**. Eden source: `{revision}`.", "",
             f"Archive SHA-256: `{metadata['archive_sha256']}`.", "",
             f"Scanned **{module_count:,} modules** / **{code_count:,} code objects** without executing client code.", "",
             "| Classification | Unique service/method pairs |", "| --- | ---: |"]
    lines.extend(f"| {name} | {count:,} |" for name, count in sorted(counts.items()))
    lines.extend(["", f"Unresolved dynamic selectors: **{len(inspector.unresolved):,}**. Decode/analysis failures: **{len(inspector.failures)}**.",
                  "", "## Interpretation", "",
                  "- **no-handler-found**: a statically traced client RPC has no matching Eden source candidate. Prioritize for review.",
                  "- **compatibility-review**: only compatibility dispatch candidates were found. Some replies are valid; others conceal missing features.",
                  "- **handler-candidate**: a dispatch source candidate exists. This is not proof of supported arguments, persistence or gameplay.",
                  "- `@skills` and `@inventory` identify returned bound objects; inventory candidates include Eden's container aliases.",
                  "- `routes.json` retains every detected call site; `routes.csv` is the sortable inventory; `unresolved.csv` preserves dynamic selectors.",
                  "", "This is a static lower bound, not an exhaustive list of everything the client or server can do. Dynamic service/method names,",
                  "cross-module wrappers, HTTP/gateway APIs, native binary calls and data-driven dispatch can escape this scan. Branches are inspected",
                  "linearly; dead, developer-only, deprecated and feature-flagged calls are included and need triage. The scanner follows literal",
                  "remote selectors, moniker factories, simple local aliases, self attributes and four rounds of remote-return wrapper summaries.",
                  "Local service GUIDs connect return summaries across modules. Explicit pinned-client bridges cover Michelle's remote park and",
                  "invCache's dynamic manager/container wrappers. Inventory methods can be local cache operations and require per-call review.",
                  "Remote method references are included, covering callbacks passed to thread/session helpers; reachability is not established.",
                  "The pinned CSPAChargedAction string dispatcher is traced explicitly, including mail sending.",
                  "Server candidates conservatively match dispatch-file literals; exact tuple matches are preferred in the evidence column.",
                  "A source candidate may represent a different guard branch. Argument/location/role restrictions and partial implementations need review.",
                  "", "## No handler found, grouped by service", "", "| Service | Count | Methods |", "| --- | ---: | --- |"])
    for service, methods in sorted(missing.items(), key=lambda item: (-len(item[1]), item[0])):
        lines.append(f"| `{service}` | {len(methods)} | " + ", ".join(f"`{method}`" for method in methods) + " |")
    lines.extend(["", "## Compatibility candidates", "", "| Route | Eden source |", "| --- | --- |"])
    for route in routes:
        if route["status"] == "compatibility-review":
            lines.append(f"| `{route['service']}.{route['method']}` | " + ", ".join(f"`{p}`" for p in route["server_candidates"]) + " |")
    (output / "README.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps(metadata, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=Path("artifacts/client-route-inventory"))
    parser.add_argument("--python-libs", type=Path)
    args = parser.parse_args()
    if args.python_libs:
        sys.path.insert(0, str(args.python_libs.resolve()))
    from xdis.bytecode import Bytecode
    from xdis.load import load_module_from_file_object
    from xdis.opcodes.opcode_2x import opcode_27

    if hashlib.sha256(args.archive.read_bytes()).hexdigest() != PINNED_SHA256:
        parser.error("Archive does not match pinned build 3662168; use the staged client.")
    inspector = Inspector(Bytecode, opcode_27)
    modules = []
    with zipfile.ZipFile(args.archive) as archive:
        for name in sorted(set(archive.namelist())):
            if not name.endswith(".pyj"):
                continue
            try:
                raw = zlib.decompress(archive.read(name))
                code = load_module_from_file_object(io.BytesIO(raw), name)[3]
                modules.append((name, list(walk(code))))
            except Exception as error:
                inspector.failures.append({"module": name, "error": str(error)})
    inspector.scan(modules)
    write_results(args.output, Path(__file__).resolve().parents[1], args.archive, inspector,
                  len(modules), sum(len(codes) for _, codes in modules))
    if inspector.failures:
        raise SystemExit("Inventory contains decode/analysis failures; inspect routes.json.")


if __name__ == "__main__":
    main()
