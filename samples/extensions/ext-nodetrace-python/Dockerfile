FROM python:3.11-slim

WORKDIR /app

RUN pip install --no-cache-dir fastapi uvicorn[standard] pydantic vais-extension

COPY main.py .

EXPOSE 8080
CMD ["python", "main.py"]
