from fastapi import FastAPI, HTTPException

from graph import agent_graph
from schemas import GenerateRequest, GenerateResponse


app = FastAPI(title="DbAnonymizer Value Pool Service")


@app.post("/generate", response_model=GenerateResponse)
async def generate_pool(req: GenerateRequest) -> GenerateResponse:
    try:
        state = {
            "request": req, "raw_output": "", "values": [],
            "error": "", "attempts": 0, "model_used": "", "tokens": 0,
        }
        result = await agent_graph.ainvoke(state)
        return GenerateResponse(
            component=req.component,
            values=result["values"],
            model_used=result["model_used"],
            tokens_spent=result["tokens"],
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
