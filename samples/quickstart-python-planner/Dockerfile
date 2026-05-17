FROM python:3.12-slim
WORKDIR /app
COPY sdk/python /sdk/vais-plugin
COPY samples/quickstart-python-planner/pyproject.toml samples/quickstart-python-planner/server.py ./
RUN pip install --no-cache-dir /sdk/vais-plugin && pip install --no-cache-dir .
USER 65532:65532
EXPOSE 8090
CMD ["python", "server.py"]
