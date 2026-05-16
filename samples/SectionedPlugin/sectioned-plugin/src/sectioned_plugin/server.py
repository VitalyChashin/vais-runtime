"""Entrypoint for the sectioned-plugin subprocess. Dispatches over stdio JSON-RPC."""
import os
import sys

# Ensure the package is importable when run as `python server.py`.
_src = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _src not in sys.path:
    sys.path.insert(0, _src)

from vais_agent_sdk import run
from sectioned_plugin.agent import invoke


def main() -> None:
    run(invoke)


if __name__ == "__main__":
    main()
