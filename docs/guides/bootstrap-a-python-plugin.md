# Bootstrap a Python plugin into the runtime

`vais plugin-push` is the canonical way to deploy a Python plugin, whether you are publishing it for the first time or updating its source. On first push the runtime automatically provisions a Python virtual environment and starts the subprocess; subsequent pushes hot-reload the subprocess in-place without a restart.

## Prerequisites

- The runtime is configured with `VAIS_PYTHON_PLUGINS_RELOAD_POLICY=DrainAndSwap`.
- `python3.11` is available in the runtime container (it is present in the standard `ghcr.io/astral-sh/uv`-based runtime image).
- The plugin directory (`plugin.yaml` + `pyproject.toml` + source) is packaged as a tar.gz archive, which `vais plugin-push` does automatically.

## Path 0 — CLI push (recommended, P11-compliant)

```bash
# First push: bootstrap + start (returns 201 Created)
vais plugin-push my-py-agent

# All subsequent pushes: hot-reload in-place (returns 200 OK)
vais plugin-push my-py-agent

# Continuous watch: auto-push on every file save (Ctrl-C to stop)
vais plugin-watch my-py-agent
```

On first push the runtime:
1. Extracts the tar.gz into `$VAIS_PYTHON_PLUGINS_DIRECTORY/my-py-agent/`.
2. Runs `python3.11 -m venv .venv` inside the plugin directory.
3. Runs `.venv/bin/pip install -q -e .` to install the plugin's declared dependencies.
4. Starts the plugin subprocess and performs the MCP handshake.
5. Returns `201 Created` with `{ "status": "Bootstrapped" }`.

On subsequent pushes it runs the DrainAndSwap hot-reload (returns `200 OK`, `"status": "Success"`).

**First push can take 30–120 s** if the dependency tree is large. The CLI waits for the server response; the default timeout is 120 s. To change it, set `VAIS_PYTHON_PLUGINS_BOOTSTRAP_TIMEOUT_SECONDS` or configure `BootstrapTimeoutSeconds` in `PythonPluginLoaderOptions`.

## Checklist before first push

| Check | How |
|-------|-----|
| `VAIS_PYTHON_PLUGINS_RELOAD_POLICY=DrainAndSwap` is set | `vais status` or runtime logs |
| `pyproject.toml` has `targetApiVersion = "0.24"` in `[tool.vais.plugin]` | `cat pyproject.toml` |
| Plugin name matches the top-level folder name used by the runtime | Must be a single path component: no `/`, `\`, or `..` |

After a successful first push, `vais plugin-status` will show the plugin as `ready`.

---

## Alternative paths (legacy / special cases)

The following paths do not require `VAIS_PYTHON_PLUGINS_RELOAD_POLICY=DrainAndSwap` and are still supported for production baking or environments without CLI access.

### Path 1 — Bake into the runtime image (production)

Add a `Dockerfile.overlay` to your plugin directory:

```dockerfile
FROM ghcr.io/astral-sh/uv:python3.11-bookworm-slim AS builder
WORKDIR /plugin
COPY pyproject.toml .
# TODO: replace with "uv pip install vais-agent-sdk" once published to PyPI
COPY path/to/python-agent-sdk /sdk
RUN uv venv .venv \
 && uv pip install --python .venv/bin/python /sdk \
 && uv pip install --python .venv/bin/python -e .

FROM ghcr.io/astral-sh/uv:python3.11-bookworm-slim
WORKDIR /plugin
COPY --from=builder /plugin/.venv .venv
COPY src src
```

Then extend your runtime's `Dockerfile` to copy the built plugin into the image:

```dockerfile
COPY --from=plugin-builder /plugin /var/lib/vais/python-plugins/my-py-agent
```

Rebuild and restart the runtime — the plugin is loaded at startup automatically.

For local dev with the standard `local-dev/dev.ps1` stack, add the plugin's build stage and `COPY` step to `local-dev/Dockerfile.runtime-overlay` (or equivalent), then run:

```bash
.\dev.ps1 start
```

### Path 2 — Mount a host directory (persistent local dev)

Start the runtime container with your plugin directory mounted as a volume:

```bash
docker run ... \
  -e VAIS_PYTHON_PLUGINS_DIRECTORY=/var/lib/vais/python-plugins \
  -v /absolute/path/to/my-py-agent:/var/lib/vais/python-plugins/my-py-agent:ro \
  vais-research-pipeline:local
```

The plugin folder must contain a `.venv` built for the container's Linux environment. Create it inside the container once, then leave it in place:

```bash
# One-time setup: create the Linux venv inside the mounted folder
docker exec -u root <container> sh -c "
  cd /var/lib/vais/python-plugins/my-py-agent &&
  python3.11 -m venv .venv &&
  .venv/bin/pip install pydantic>=2.8 vais-agent-sdk
"
docker restart <container>
```

After that, `vais plugin-push my-py-agent` hot-reloads source without a restart; only dependency changes require recreating the venv.

### Path 3 — docker cp (quick one-off test)

For a throwaway test without touching the compose file or Dockerfile:

```bash
# 1. Copy plugin source into the running container
docker exec vais-runtime mkdir -p /var/lib/vais/python-plugins/my-py-agent
docker cp plugin.yaml   vais-runtime:/var/lib/vais/python-plugins/my-py-agent/
docker cp pyproject.toml vais-runtime:/var/lib/vais/python-plugins/my-py-agent/
docker cp src            vais-runtime:/var/lib/vais/python-plugins/my-py-agent/

# 2. Create or copy a Linux venv with the required packages
#    (fastest: copy from an existing plugin that has vais-agent-sdk + pydantic)
docker exec vais-runtime sh -c \
  "cp -r /var/lib/vais/python-plugins/sgr-analyst/.venv \
         /var/lib/vais/python-plugins/my-py-agent/.venv"

# 3. Fix ownership — docker cp lands files as root but the runtime runs as vais
docker exec -u root vais-runtime chown -R vais:vais \
  /var/lib/vais/python-plugins/my-py-agent

# 4. Restart to trigger the plugin scan (stop + start, not docker restart)
docker stop vais-runtime && docker start vais-runtime
```

This bootstrap survives normal `docker stop`/`docker start` cycles but is lost on `docker rm` (container recreation). Use Path 1 or 2 for anything persistent.

---

## After bootstrap — hot-reload and watch

Once the plugin is loaded, skip the restart step for all future source changes:

```bash
# One-shot hot-reload: push ./src into the running subprocess
vais plugin-push my-py-agent

# Continuous watch: auto-push on every file save (Ctrl-C to stop)
vais plugin-watch my-py-agent
```

Both commands work as long as `vais plugin-status` shows the plugin as `ready`. If the status shows `unavailable`, the subprocess failed to start — check runtime logs for the error.
