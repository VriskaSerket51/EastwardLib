"""Use Pixpil's quest importer while retaining newer GraphML transition metadata."""

import importlib.util
import json
import pathlib
import sys
from xml.etree import ElementTree as ET


def import_quest_graph(file, gii):
    sys.path.insert(0, str(pathlib.Path(gii).resolve() / "lib"))
    source = pathlib.Path(gii) / "lib/mock/asset/tools/ML2QuestScheme.py"
    spec = importlib.util.spec_from_file_location("quest", source)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    parser = module.QuestSchemeParser()
    if not parser.parse(str(file)):
        raise ValueError(f"Quest importer rejected {file}")
    data = parser.saveToData()
    document = ET.parse(file)
    namespace = {"g": "http://graphml.graphdrawing.org/xmlns"}
    keys = {
        key.attrib["id"]
        for key in document.findall("g:key", namespace)
        if key.get("attr.name") == "transition" and key.get("for") == "node"
    }
    transitions = {}
    for node in document.findall(".//g:node", namespace):
        values = [
            value
            for value in node.findall("g:data", namespace)
            if value.get("key") in keys
        ]
        if len(values) > 1:
            raise ValueError(f"Duplicate transition metadata: {node.get('id')}")
        if values:
            transitions[node.attrib["id"]] = json.loads(values[0].text)

    def restore(nodes):
        for node in nodes:
            node["transition"] = transitions.pop(node["id"], False)
            restore(node.get("children", []))

    restore(data["nodes"])
    if transitions:
        raise ValueError(
            f"Transition metadata belongs to unimported nodes: {list(transitions)}"
        )
    return data


if __name__ == "__main__":
    result = import_quest_graph(
        sys.argv[1], sys.argv[3] if len(sys.argv) > 3 else "../gii"
    )
    pathlib.Path(sys.argv[2]).write_text(
        json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8"
    )
