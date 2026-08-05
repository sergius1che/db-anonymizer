import os
import re

from litellm import completion


class LLMClient:
    def __init__(self, provider: str | None = None, model: str | None = None):
        self.provider = provider or os.getenv("LLM_PROVIDER", "openai")
        self.model = model or os.getenv("LLM_MODEL", "gpt-4o-mini")

    @property
    def model_name(self) -> str:
        return f"{self.provider}/{self.model}"

    def invoke(self, prompt: str) -> tuple[str, str, int]:
        """Returns (content, model_used, tokens_spent)."""
        if self.provider == "fake":
            match = re.search(r"ровно (\d+)", prompt)
            count = int(match.group(1)) if match else 64
            lines = [f"ТестЗначение{i:04d}" for i in range(1, count + 1)]
            return "\n".join(lines), "fake/fake-model", 0
        response = completion(
            model=f"{self.provider}/{self.model}",
            messages=[{"role": "user", "content": prompt}],
        )
        usage = getattr(response, "usage", None)
        tokens = usage.total_tokens if usage is not None else 0
        return response.content, response.model, tokens


def get_llm() -> LLMClient:
    return LLMClient()
