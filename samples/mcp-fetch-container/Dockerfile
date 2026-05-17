# Reference container image for the official mcp-server-fetch (PyPI).
# Built from the mcp-stdio-template Dockerfile with the fetch package added.
#
# See ../mcp-stdio-template/README.md for the underlying pattern.

FROM python:3.12-slim
WORKDIR /app

# Install fastmcp first so it pulls its preferred httpx version (>=0.28.1).
# mcp-server-fetch's older pinned httpx then becomes the lower bound; pip picks the
# higher of the two for the final install.
RUN pip install --no-cache-dir fastmcp==3.3.1
RUN pip install --no-cache-dir mcp-server-fetch==2025.4.7

# Reuse the template's bridge verbatim — copied into this directory so the
# build context is self-contained (Docker doesn't allow ../ in COPY).
COPY bridge.py .

EXPOSE 7000
CMD ["python", "bridge.py"]
