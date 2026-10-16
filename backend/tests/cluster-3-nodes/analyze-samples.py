"""Analyse un fichier d'échantillons (sampler.sh) : exclusivité par média, capacité par nœud, réservations. Usage : analyze-samples.py <tsv>"""
import sys, collections
rows = [l.rstrip('\n').split('\t') for l in open(sys.argv[1]) if l.strip()]
by_t = collections.defaultdict(list)
for r in rows: by_t[r[0]].append(r)
per_node, dup_media, resv_bad, epochs = collections.Counter(), 0, 0, collections.defaultdict(set)
for t, rs in by_t.items():
    run = [r for r in rs if r[2] == 'running']
    for n, v in collections.Counter(r[3] for r in run).items(): per_node[n] = max(per_node[n], v)
    dup_media += sum(1 for v in collections.Counter(r[8] and r[1] for r in run).values() if v > 1)
    resv_bad += sum(1 for r in run if int(r[9]) != 1)
    for r in run: epochs[r[8]].add((r[3], r[4]))
print(f"échantillons={len(by_t)} | pic de 'running' par nœud={dict(per_node)} | instants avec un même média 'running' >1 fois={dup_media} | 'running' sans 1 réservation={resv_bad}")
multi = {k: sorted(v) for k, v in epochs.items() if len(v) > 1}
print("exécutions ayant changé de (nœud, époque):", multi or "aucune")
