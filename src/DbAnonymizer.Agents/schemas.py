from pydantic import BaseModel, Field

class GenerateRequest(BaseModel):
    semantic_type: str          # "FirstName", "LastName", "City", "CompanyName"
    component: str              # "first_name", "last_name", "city"
    pool_size: int = Field(ge=100, le=100_000, default=300)
    language: str = "ru"

class GenerateResponse(BaseModel):
    component: str
    values: list[str]
    model_used: str
    tokens_spent: int
