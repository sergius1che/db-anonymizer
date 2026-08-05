# Value Pool Service: FastAPI + LangGraph + LiteLLM.
# Зависимости — в requirements.txt (зеркало pyproject.toml); поставим их отдельным
# слоем, чтобы не тащить setuptools/editable-установку в рантайм.
FROM python:3.12-slim
WORKDIR /app
COPY src/DbAnonymizer.Agents/requirements.txt requirements.txt
RUN pip install --no-cache-dir -r requirements.txt
COPY src/DbAnonymizer.Agents/ .
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
