"""Entrypoint for the langgraph-researcher-live plugin subprocess."""
import os
import sys

# Ensure the package is importable when run as a script (python server.py).
_src = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _src not in sys.path:
    sys.path.insert(0, _src)

from vais_agent_sdk import run
from langgraph_researcher_live.agent import invoke


def main() -> None:
    run(invoke)


if __name__ == "__main__":
    main()
