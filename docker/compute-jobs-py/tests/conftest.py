import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))  # computejobs (source tree; in images it sits on /app)
sys.path.insert(0, HERE)                   # dummy.py, importable by the spawned job processes
