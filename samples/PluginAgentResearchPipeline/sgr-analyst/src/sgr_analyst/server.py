"""Entrypoint for the sgr-analyst plugin subprocess."""
import os
import sys

_src = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _src not in sys.path:
    sys.path.insert(0, _src)

from vais_agent_sdk import run
from sgr_analyst.agent import invoke


def main() -> None:
    run(invoke)


if __name__ == "__main__":
    main()
