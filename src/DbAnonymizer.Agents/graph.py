from typing import TypedDict

from langgraph.graph import END, StateGraph
from langgraph.graph.state import CompiledStateGraph

from llm_client import get_llm
from prompts import PROMPTS
from schemas import GenerateRequest

MAX_ATTEMPTS = 3


class AgentState(TypedDict):
    request: GenerateRequest
    raw_output: str
    values: list[str]
    error: str
    attempts: int
    model_used: str
    tokens: int


def build_graph() -> CompiledStateGraph:
    graph = StateGraph(AgentState)

    def validate(state: AgentState) -> AgentState:
        req = state["request"]
        if req.component not in PROMPTS:
            raise ValueError(f"No prompt for component: {req.component}")
        return state

    def generate(state: AgentState) -> AgentState:
        # Накапливаем уникальные значения между попытками и на retry просим у LLM
        # только недостающий остаток — раньше каждая попытка запрашивала весь
        # pool_size заново и отбрасывала уже сгенерированные строки предыдущей
        # попытки, что делало retry бессмысленным при большом pool_size
        req = state["request"]
        existing = state.get("values", [])
        remaining = req.pool_size - len(existing)
        llm = get_llm()
        prompt = PROMPTS[req.component].format(count=remaining)
        content, model_used, tokens = llm.invoke(prompt)
        lines = [line.strip() for line in content.split("\n")
                 if line.strip() and not line.strip().startswith(("#", "//", "1."))]
        seen = set(existing)
        for line in lines:
            if line in seen:
                continue
            existing.append(line)
            seen.add(line)
            if len(existing) >= req.pool_size:
                break
        state["values"] = existing
        state["attempts"] = state.get("attempts", 0) + 1
        state["model_used"] = model_used
        state["tokens"] = state.get("tokens", 0) + tokens
        return state

    def validate_count(state: AgentState) -> str:
        if len(state["values"]) < state["request"].pool_size:
            if state["attempts"] >= MAX_ATTEMPTS:
                raise RuntimeError(
                    f"Failed to generate {state['request'].pool_size} values "
                    f"for {state['request'].component} after {MAX_ATTEMPTS} attempts"
                )
            return "retry"
        return "done"

    graph.add_node("validate", validate)
    graph.add_node("generate", generate)
    graph.set_entry_point("validate")
    graph.add_edge("validate", "generate")
    graph.add_conditional_edges("generate", validate_count, {
        "retry": "generate",
        "done": END,
    })
    return graph.compile()


agent_graph = build_graph()
