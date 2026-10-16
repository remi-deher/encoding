"""Relance la découverte de la bibliothèque test-lib (après ajout de médias) et attend la fin. Usage : discover.py"""
import os, sys, json, time
sys.path.insert(0, '.')
from cl import *
st = json.load(open(os.environ.get('STATE_FILE', '/tmp/enc-cluster-state.json')))
a = Api('node1'); a.login(); a.post(f"/api/v1/libraries/{st['libraryId']}/discover"); time.sleep(8)
s, w = a.get(f"/api/v1/libraries/{st['libraryId']}/workflow"); print(w['scan']['state'], len(w['files']), "fichiers")
