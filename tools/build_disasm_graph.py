"""Rebuild the SMW disassembly call graph (reference/smw-disasm/graph/).

Nodes = human-named routines (labels that aren't auto CODE_/DATA_/ReturnXXXXXX).
Edges = JSR/JSL/JMP/JML from the enclosing named routine to a named target.
Calls to auto-labelled basic blocks are not edges (navigation map, not full CFG).

Usage (with graphify's python):
  python tools/build_disasm_graph.py            # extract + cluster, prints communities
  python tools/build_disasm_graph.py --relabel  # regen report + html from
                                                # graph/disasm_labels.json
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DIR = ROOT / "reference" / "smw-disasm"
OUT = DIR / "graph"

AUTO = re.compile(r"^(?:CODE|DATA|ADDR)_[0-9A-F]{6}$|^Return[0-9A-F]{6}$|^\$")
EMBED = re.compile(r"^(?:CODE_|DATA_|Return|Empty)([0-9A-F]{6})$")
LABELLED = re.compile(r"^(\S+?):\s*(.*)$")
HEXBYTES = re.compile(r"^((?:[0-9A-F]{2} )+)")
CALL = re.compile(r"\b(?:JSR|JSL|JMP|JML)(?:\.[WLB])?\s+([A-Za-z_][\w+&\-]*)")


def extract():
    defs, edges, referenced = {}, {}, {}
    for f in sorted(DIR.glob("bank_*.asm")):
        rel = f"reference/smw-disasm/{f.name}"
        cur_routine = None
        next_addr = None  # address of the next line, when derivable
        for line in f.read_text(encoding="utf-8", errors="replace").splitlines():
            m = LABELLED.match(line)
            if not m:
                next_addr = None  # unlabelled data rows: lose address lock
                continue
            label, rest = m.group(1), m.group(2)
            em = EMBED.match(label)
            addr = int(em.group(1), 16) if em else next_addr
            bm = HEXBYTES.match(rest)
            nbytes = len(bm.group(1).split()) if bm else 0
            next_addr = addr + nbytes if addr is not None and nbytes else None

            code = rest.split(";", 1)[0]
            comment = rest.split(";", 1)[1].strip() if ";" in rest else ""
            if not AUTO.match(label):
                cur_routine = label
                if label not in defs:
                    defs[label] = {
                        "id": label,
                        "label": label,
                        "file_type": "code",
                        "source_file": rel,
                        "source_location": f"${addr:06X}" if addr is not None else "",
                        "description": comment,
                    }
            cm = CALL.search(code)
            if cm and cur_routine:
                target = cm.group(1)
                if not AUTO.match(target) and target != cur_routine:
                    edges.setdefault((cur_routine, target), rel)
                    referenced.setdefault(target, rel)

    # Analysis docs: link each .md to every routine label it mentions.
    doc_edges = {}
    known = re.compile(
        r"(?<![\w$])`?(" + "|".join(
            re.escape(k) for k in sorted(defs, key=len, reverse=True)) + r")(?![\w])")
    for doc in sorted(DIR.glob("*.md")):
        rel = f"reference/smw-disasm/{doc.name}"
        text = doc.read_text(encoding="utf-8")
        for label in {m.group(1) for m in known.finditer(text)}:
            doc_edges[(doc.name, label)] = rel

    # Nodes = edge endpoints only (a navigation map, not a label inventory).
    nodes = {}
    for (s, t), rel in edges.items():
        for endpoint in (s, t):
            nodes[endpoint] = defs.get(endpoint) or {
                "id": endpoint, "label": endpoint, "file_type": "code",
                "source_file": referenced.get(endpoint, rel),
                "source_location": "", "description": "",
            }
    for (doc, label), rel in doc_edges.items():
        nodes.setdefault(label, defs[label])
        nodes.setdefault(doc, {
            "id": doc, "label": doc, "file_type": "document",
            "source_file": rel, "source_location": "", "description": "",
        })
    return {
        "nodes": sorted(nodes.values(), key=lambda n: n["id"]),
        "edges": [
            {"source": s, "target": t, "relation": "calls",
             "confidence": "EXTRACTED", "source_file": rel}
            for (s, t), rel in sorted(edges.items())
        ] + [
            {"source": doc, "target": label, "relation": "documents",
             "confidence": "EXTRACTED", "source_file": rel}
            for (doc, label), rel in sorted(doc_edges.items())
        ],
        "hyperedges": [],
        "input_tokens": 0,
        "output_tokens": 0,
    }


def main():
    from graphify.analyze import god_nodes, suggest_questions, surprising_connections
    from graphify.build import build_from_json
    from graphify.cluster import cluster, score_all
    from graphify.export import to_html, to_json
    from graphify.report import generate

    relabel = "--relabel" in sys.argv
    if relabel:
        extraction = json.loads((OUT / "disasm_extract.json").read_text(encoding="utf-8"))
        analysis = json.loads((OUT / "disasm_analysis.json").read_text(encoding="utf-8"))
        communities = {int(k): v for k, v in analysis["communities"].items()}
    else:
        extraction = extract()
        (OUT / "disasm_extract.json").write_text(
            json.dumps(extraction, indent=1, ensure_ascii=False), encoding="utf-8")

    G = build_from_json(extraction, root=str(ROOT))
    if G.number_of_nodes() == 0:
        sys.exit("ERROR: empty graph")

    if not relabel:
        communities = cluster(G)
        analysis = {
            "communities": {str(k): v for k, v in communities.items()},
            "cohesion": score_all(G, communities),
            "gods": god_nodes(G),
            "surprises": surprising_connections(G, communities),
        }
        (OUT / "disasm_analysis.json").write_text(
            json.dumps(analysis, indent=1, ensure_ascii=False), encoding="utf-8")

    labels_file = OUT / "disasm_labels.json"
    labels = ({int(k): v for k, v in json.loads(labels_file.read_text(encoding="utf-8")).items()}
              if relabel and labels_file.exists()
              else {cid: f"Community {cid}" for cid in communities})

    cohesion = {int(k): v for k, v in analysis["cohesion"].items()}
    detection = {"total_files": len(list(DIR.glob("bank_*.asm"))), "total_words": 0,
                 "files": {"code": [str(p) for p in DIR.glob("bank_*.asm")]},
                 "skipped_sensitive": []}
    questions = suggest_questions(G, communities, labels)
    report = generate(G, communities, cohesion, labels, analysis["gods"],
                      analysis["surprises"], detection,
                      {"input": 0, "output": 0}, str(DIR),
                      suggested_questions=questions)
    (OUT / "GRAPH_REPORT.md").write_text(report, encoding="utf-8")
    to_json(G, communities, str(OUT / "graph.json"), force=True, community_labels=labels)
    to_html(G, communities, str(OUT / "graph.html"), community_labels=labels)

    print(f"{G.number_of_nodes()} nodes, {G.number_of_edges()} edges, "
          f"{len(communities)} communities")
    if not relabel:
        for cid, members in sorted(communities.items(), key=lambda kv: -len(kv[1])):
            print(f"[{cid}] ({len(members)}): {', '.join(sorted(members)[:8])}")


if __name__ == "__main__":
    main()
