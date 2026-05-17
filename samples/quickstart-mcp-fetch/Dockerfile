FROM python:3.12-slim
WORKDIR /app
RUN pip install --no-cache-dir fastmcp==3.3.1
COPY server.py .
EXPOSE 3000
CMD ["python", "server.py"]
