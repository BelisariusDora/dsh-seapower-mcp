# Sea Power compatible module entry. Keep existing client configurations working.
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parent/'modules'))
from runtime_layout import run_entry
run_entry('mcp/mcp_server.py',globals())
