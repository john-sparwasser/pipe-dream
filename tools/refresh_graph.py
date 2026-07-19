"""Rebuild the editor's graphify knowledge graph from src/ (AST, no LLM).

Run via ../refresh-graph.ps1 (resolves the graphify interpreter). Outputs to
graphify-out/{graph.json,GRAPH_REPORT.md}; HTML is exported by the launcher.
Community labels are derived from each cluster's dominant source file so they
stay meaningful across rebuilds as the code changes.
"""
import json
from collections import Counter
from pathlib import Path
from graphify.detect import detect
from graphify.extract import collect_files, extract
from graphify.build import build_from_json
from graphify.cluster import cluster, score_all
from graphify.analyze import god_nodes, surprising_connections, suggest_questions
from graphify.report import generate

PROJ = Path(__file__).resolve().parent.parent
SRC = PROJ / "src"
GO = PROJ / "graphify-out"
ROOT = str(PROJ)
GO.mkdir(exist_ok=True)

det = detect(SRC)
code_files = []
for f in det.get("files", {}).get("code", []):
    p = Path(f)
    code_files.extend(collect_files(p) if p.is_dir() else [p])

extraction = extract(code_files, cache_root=PROJ)
(GO / ".graphify_extract.json").write_text(json.dumps(extraction, ensure_ascii=False), encoding="utf-8")
(GO / ".graphify_detect.json").write_text(json.dumps(det, ensure_ascii=False), encoding="utf-8")

G = build_from_json(extraction, root=ROOT, directed=True)
if G.number_of_nodes() == 0:
    raise SystemExit("ERROR: extraction produced no nodes")

communities = cluster(G)
cohesion = score_all(G, communities)
gods = god_nodes(G)
surprises = surprising_connections(G, communities)


def label_for(members):
    """Dominant source-file stem in the community, else a generic name."""
    stems = Counter()
    for nid in members:
        sf = G.nodes.get(nid, {}).get("source_file") or ""
        stem = Path(sf).stem
        if stem:
            stems[stem] += 1
    return stems.most_common(1)[0][0] if stems else "misc"


labels = {cid: label_for(mem) for cid, mem in communities.items()}
# disambiguate duplicate labels (two clusters dominated by the same file)
seen = Counter()
for cid in list(labels):
    name = labels[cid]
    seen[name] += 1
    if seen[name] > 1:
        labels[cid] = f"{name} ({seen[name]})"

questions = suggest_questions(G, communities, labels)

# graph.json can pre-exist and be larger mid-refresh; write it directly (single-graph repo).
from graphify.export import to_json
GO.joinpath("graph.json").unlink(missing_ok=True)
to_json(G, communities, str(GO / "graph.json"))

report = generate(G, communities, cohesion, labels, gods, surprises, det,
                  {"input": 0, "output": 0}, ROOT, suggested_questions=questions)
(GO / "GRAPH_REPORT.md").write_text(report, encoding="utf-8")
(GO / ".graphify_analysis.json").write_text(json.dumps({
    "communities": {str(k): v for k, v in communities.items()},
    "cohesion": {str(k): v for k, v in cohesion.items()},
    "gods": gods, "surprises": surprises, "questions": questions,
}, ensure_ascii=False), encoding="utf-8")
(GO / ".graphify_labels.json").write_text(
    json.dumps({str(k): v for k, v in labels.items()}, ensure_ascii=False), encoding="utf-8")

print(f"{len(code_files)} files -> {G.number_of_nodes()} nodes, "
      f"{G.number_of_edges()} edges, {len(communities)} communities")
for cid, mem in sorted(communities.items(), key=lambda kv: -len(kv[1])):
    print(f"  [{labels[cid]}] {len(mem)} nodes")
