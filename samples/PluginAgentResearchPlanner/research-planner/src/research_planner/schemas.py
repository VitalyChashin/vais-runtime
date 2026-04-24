from pydantic import BaseModel, Field


class DecomposeTaskArgs(BaseModel):
    question: str = Field(description="The research question to decompose.")
    max_subquestions: int = Field(default=5, ge=1, le=10)


class ScoredPlan(BaseModel):
    coverage_score: float = Field(ge=0.0, le=1.0)
    missing_angles: list[str]
    rationale: str


class SummarizeFindingsArgs(BaseModel):
    question: str
    findings: list[str] = Field(min_length=1)
    max_length_chars: int = Field(default=2000, ge=200)
