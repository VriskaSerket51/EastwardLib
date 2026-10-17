"""Read restored assets with Pixpil's public Gii tools; never start Gii or the game.

Usage: python tests/audit_source_project.py OUTPUT GII_CHECKOUT
Requires Pillow, six, PyYAML and Gii's bundled xlrd/psd_tools.
Writes generated FSM comparison inputs beside the source project.
"""

import contextlib
import ast
import array
import hashlib
import importlib.util
import io
import json
import pathlib
import sys
import types

root = pathlib.Path(sys.argv[1]).resolve()
gii = pathlib.Path(sys.argv[2]).resolve()
sys.path.insert(0, str(gii / "lib"))
sys.path.append(str(gii / "lib/3rdparty/common"))
sys.path.append(str(gii / "lib/3rdparty/windows"))
sys.modules["xxhash"] = types.SimpleNamespace(xxh64=lambda data: hashlib.sha256(data))
# The public PSD reader uses Python 2 array method names for RLE data.
class CompatibleArray(array.array):
    fromstring = array.array.frombytes
    tostring = array.array.tobytes
array.array = CompatibleArray
from util.AsepriteFile.AsepriteFile import ASEDocument
from psd_tools import PSDImage
import xlrd
from import_quest_graph import import_quest_graph

report = json.loads((root / ".eastward-project.json").read_text(encoding="utf-8"))
counts = {"aseFiles": 0, "frames": 0, "tags": 0, "psdFiles": 0, "psdLayers": 0, "workbooks": 0, "sheets": 0, "questGraphs": 0, "portalGraphs": 0, "fsmGraphs": 0}
errors = {}
comparisons = []

def load_tool(name):
    spec = importlib.util.spec_from_file_location(name, gii / "lib/mock/asset/tools" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

fsm_module = load_tool("ml2fsm")
portal_module = load_tool("ML2PortalGraph")
workbook_importer = {"xlrd": xlrd, "re": __import__("re"), "logging": __import__("logging")}
source = ast.parse((gii / "lib/mock/asset/DataXLSAsset.py").read_text(encoding="utf-8"))
functions = [node for node in source.body if isinstance(node, ast.FunctionDef) and node.name in ("cellValue", "convertSheetToData", "convertWorkbookToData")]
exec(compile(ast.Module(body=functions, type_ignores=[]), "DataXLSAsset.py", "exec"), workbook_importer)

def normalized(value):
    if isinstance(value, dict):
        return {key: normalized(item) for key, item in value.items()}
    if isinstance(value, list):
        return sorted((normalized(item) for item in value), key=lambda item: json.dumps(item, sort_keys=True))
    return value

for file in (root / "game/asset").rglob("*"):
    if not file.is_file():
        continue
    try:
        if file.suffix == ".ase":
            document = ASEDocument()
            document.load(str(file))
            assert document.frames
            assert all(0 <= tag.frameFrom <= tag.frameTo < len(document.frames) for tag in document.tags)
            counts["aseFiles"] += 1
            counts["frames"] += len(document.frames)
            counts["tags"] += len(document.tags)
        elif file.suffix == ".psd":
            document = PSDImage.load(str(file))
            def layers(items):
                count = 0
                for layer in items:
                    if hasattr(layer, "layers"):
                        count += layers(layer.layers)
                    else:
                        assert layer.as_PIL() is not None
                        count += 1
                return count
            counts["psdLayers"] += layers(document.layers)
            if not document.layers:
                assert document.as_PIL() is not None
            counts["psdFiles"] += 1
        elif file.suffix == ".xls":
            book = xlrd.open_workbook(str(file))
            assert book.sheets()
            actual, metadata = workbook_importer["convertWorkbookToData"](book)
            assert len(actual) == len(book.sheets()), "Gii did not recognize every sheet"
            if len(sys.argv) > 3:
                expected_file = pathlib.Path(sys.argv[3]) / (str(file.relative_to(root / "game/asset")) + ".json")
                expected = json.loads(expected_file.read_text(encoding="utf-8"))
                assert actual == expected["data"], "Gii XLS importer changed runtime values"
                assert metadata == expected["metadata"], "Gii XLS importer changed sheet metadata"
            counts["workbooks"] += 1
            counts["sheets"] += len(book.sheets())
        elif file.name.endswith(".quest.graphml"):
            expected = json.loads(pathlib.Path(str(file) + ".runtime.json").read_text(encoding="utf-8"))
            actual = import_quest_graph(file, gii)
            assert actual == expected, "Quest importer changed runtime data"
            counts["questGraphs"] += 1
        elif file.name.endswith(".portal.graphml"):
            expected = json.loads(pathlib.Path(str(file) + ".runtime.json").read_text(encoding="utf-8"))
            parser = portal_module.PortalGraphParser()
            parser.parse(str(file))
            assert normalized(parser.saveToData()) == normalized(expected), "Portal importer changed runtime data"
            counts["portalGraphs"] += 1
        elif file.name.endswith(".fsm.graphml"):
            parser = fsm_module.FSMParser()
            with contextlib.redirect_stdout(io.StringIO()):
                code = parser.parse(str(file))
            comparisons.append({"path": str(file.relative_to(root)), "original": pathlib.Path(str(file) + ".runtime.lua").read_text(encoding="utf-8"), "recompiled": code})
            counts["fsmGraphs"] += 1
    except Exception as error:
        errors[str(file.relative_to(root))] = str(error)

pathlib.Path(str(root) + ".fsm.json").write_text(json.dumps(comparisons), encoding="utf-8")
print(json.dumps({"counts": counts, "errors": errors}))
sys.exit(bool(errors))
