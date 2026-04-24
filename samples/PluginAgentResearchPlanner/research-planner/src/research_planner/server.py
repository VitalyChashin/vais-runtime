"""Research-planner MCP server. Spawned by the Vais runtime over stdio."""
import os
import sys

# When run as a script (python src/research_planner/server.py) the package
# context is absent, so relative imports fail. Add the src/ directory so
# absolute imports resolve correctly in both script and module execution modes.
_src_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _src_dir not in sys.path:
    sys.path.insert(0, _src_dir)

from mcp.server.fastmcp import FastMCP
from research_planner.planner import Planner
from research_planner.schemas import DecomposeTaskArgs, ScoredPlan, SummarizeFindingsArgs

mcp = FastMCP("research-planner")
_planner = Planner()


@mcp.tool()
def decompose_task(args: DecomposeTaskArgs) -> list[str]:
    """Break a research question into answerable sub-questions."""
    return _planner.decompose(args.question, args.max_subquestions)


@mcp.tool()
def score_plan_completeness(
    question: str, subquestions: list[str]
) -> ScoredPlan:
    """Judge whether the sub-question list covers the original question."""
    return _planner.score(question, subquestions)


@mcp.tool()
def summarize_findings(args: SummarizeFindingsArgs) -> str:
    """Reduce a list of findings to a single coherent summary."""
    return _planner.summarize(args.question, args.findings, args.max_length_chars)


def main() -> None:
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
