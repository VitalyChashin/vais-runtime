"""Heuristic planning logic — no LLM required, fully hermetic."""
from __future__ import annotations

import re
import textwrap

from .schemas import ScoredPlan


_QUESTION_TEMPLATES = [
    "What are the primary causes of {topic}?",
    "What is the current state of {topic}?",
    "What are the economic implications of {topic}?",
    "What are the technological factors driving {topic}?",
    "What policy or regulatory context shapes {topic}?",
    "What are the key stakeholders involved in {topic}?",
    "What are the historical trends behind {topic}?",
    "What does the research literature say about {topic}?",
    "What are the main open questions around {topic}?",
    "What are the projected future developments for {topic}?",
]


def _extract_topic(question: str) -> str:
    question = question.strip().rstrip("?")
    for prefix in ("what is", "what are", "why is", "why are", "how does", "how do", "explain"):
        if question.lower().startswith(prefix):
            question = question[len(prefix):].strip()
            break
    return question or question


class Planner:
    def decompose(self, question: str, max_subquestions: int) -> list[str]:
        topic = _extract_topic(question)
        templates = _QUESTION_TEMPLATES[:max_subquestions]
        return [t.format(topic=topic) for t in templates]

    def score(self, question: str, subquestions: list[str]) -> ScoredPlan:
        topic = _extract_topic(question).lower()
        topic_words = set(re.findall(r"\w+", topic))

        covered = sum(
            1 for sq in subquestions
            if any(w in sq.lower() for w in topic_words)
        )
        coverage = min(1.0, covered / max(len(subquestions), 1))

        missing: list[str] = []
        if coverage < 0.5:
            missing.append("More sub-questions should reference the core topic.")
        if len(subquestions) < 3:
            missing.append("Plan has fewer than 3 sub-questions — coverage may be shallow.")

        return ScoredPlan(
            coverage_score=round(coverage, 2),
            missing_angles=missing,
            rationale=(
                f"{covered}/{len(subquestions)} sub-questions reference topic keywords. "
                f"Score: {round(coverage, 2):.0%}."
            ),
        )

    def summarize(self, question: str, findings: list[str], max_length_chars: int) -> str:
        header = f"Research summary: {question}\n\n"
        body = "\n\n".join(
            f"{i + 1}. {finding.strip()}" for i, finding in enumerate(findings)
        )
        full = header + body
        if len(full) > max_length_chars:
            full = textwrap.shorten(full, width=max_length_chars, placeholder=" [truncated]")
        return full
