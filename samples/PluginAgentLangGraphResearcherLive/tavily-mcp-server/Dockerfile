FROM python:3.11-slim
WORKDIR /app
RUN pip install --no-cache-dir "mcp[cli]>=1.0,<2" "tavily-python>=0.5"
COPY server.py .
EXPOSE 8000
CMD ["python", "server.py"]
