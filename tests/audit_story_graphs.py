"""Round-trip restored story graphs through Pixpil's public importer."""

import contextlib
import importlib.util
import io
import json
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
gii = pathlib.Path(sys.argv[2] if len(sys.argv) > 2 else "../gii").resolve()
sys.path.insert(0, str(gii / "lib"))
spec = importlib.util.spec_from_file_location(
    "story", gii / "lib/mock/asset/tools/ml2story.py"
)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
count = 0
for original in root.rglob("*.story.graphml.runtime.json"):
    graph = pathlib.Path(str(original).removesuffix(".runtime.json"))
    parser = module.StoryGraphParser()
    with contextlib.redirect_stdout(io.StringIO()):
        if not parser.parse(str(graph)):
            raise ValueError(f"Story importer rejected {graph}")
    expected = json.loads(original.read_text(encoding="utf-8"))
    actual = parser.saveToData()
    if actual != expected:
        raise ValueError(f"Story importer changed semantics: {graph}")
    count += 1
if not count:
    raise ValueError("No story graphs with runtime comparison data found")
print(json.dumps({"storyGraphs": count, "roundTrip": "passed"}))
