# Run Python plugins on Windows

**v0.23.** Python plugins work on Windows, but Git Bash's MSYS path-conversion layer introduces several footguns: paths get rewritten before the runtime sees them, volume mounts get prefixed with drive letters, and interpreter shebangs break. This guide documents each symptom and the fix.

---

## Background: MSYS path conversion

Git Bash ships with an MSYS2 runtime that automatically rewrites POSIX-style paths to Windows paths before passing arguments to native executables. For example:

```bash
# You type:
vais apply -f /var/lib/vais/plugins/my-plugin/plugin.yaml

# MSYS rewrites it to (approximately):
vais apply -f C:\Program Files\Git\var\lib\vais\plugins\my-plugin\plugin.yaml
```

The rewriting fires on strings that look like absolute POSIX paths (`/something`). It does not fire on relative paths (`./something`, `../something`) or Windows-native paths (`C:\something`).

---

## Symptom 1: `plugin.yaml` interpreter path not found

**What you see:** plugin fails to start; runtime log says `interpreter '.venv/bin/python' not found`.

**Cause:** The `interpreter` field in `plugin.yaml` uses the Linux convention (`.venv/bin/python`). On Windows, the virtual environment layout is `Scripts\python.exe`, not `bin/python`.

**Fix:** Use the Windows layout in `plugin.yaml` when the runtime runs natively (outside Docker):

```yaml
spec:
  python:
    interpreter: .venv/Scripts/python.exe
```

When running inside Docker (Linux container), keep `.venv/bin/python`. If you need a single `plugin.yaml` that works on both, build two variants or detect the OS at Docker image build time via a build argument.

---

## Symptom 2: Docker volume mounts get a drive letter prepended

**What you see:** `docker run` fails with "invalid volume specification" or the plugin directory is not found inside the container.

**Cause:** Git Bash rewrites `/path/to/plugins` to `C:\path\to\plugins` before passing the `-v` flag to Docker. Docker (which expects a Linux-style path for the container side) rejects or misinterprets this.

**Fix A — Double the leading slash (`//`):** MSYS leaves paths starting with `//` alone because it treats them as UNC prefixes.

```bash
docker run -d \
  -v "//$(pwd)/my-planner:/var/lib/vais/plugins/my-planner" \
  ...
```

**Fix B — Set `MSYS_NO_PATHCONV=1`:** Disables all MSYS path rewriting for the duration of the command.

```bash
MSYS_NO_PATHCONV=1 docker run -d \
  -v "$(pwd)/my-planner:/var/lib/vais/plugins/my-planner" \
  ...
```

**Fix C — Use PowerShell or cmd.exe** for the `docker run` invocation (see [Symptom 4](#symptom-4-use-powershell-to-avoid-all-rewriting)).

---

## Symptom 3: `vais apply -f /absolute/path/to/manifest.yaml` resolves incorrectly

**What you see:** `vais apply -f /home/user/manifests/agent.yaml` fails with "file not found".

**Cause:** MSYS converts the absolute path before the CLI sees it.

**Fix:** Use a relative path or a Windows-native path:

```bash
# Relative path — no rewriting
vais apply -f ./manifests/agent.yaml

# Windows-native path — no rewriting
vais apply -f 'C:\Users\user\manifests\agent.yaml'

# Or disable rewriting for this command
MSYS_NO_PATHCONV=1 vais apply -f /home/user/manifests/agent.yaml
```

---

## Symptom 4: Use PowerShell to avoid all rewriting

PowerShell has no MSYS layer and passes paths to subprocesses unchanged. If you run commands frequently from Git Bash and hit friction, consider switching to PowerShell for `vais` and `docker` invocations:

```powershell
# PowerShell — no MSYS path rewriting
docker run -d `
  -v "${PWD}\my-planner:/var/lib/vais/plugins/my-planner" `
  -e ANTHROPIC_API_KEY="$env:ANTHROPIC_API_KEY" `
  vais-agents-runtime:local
```

The Vais CLI works identically in PowerShell.

---

## Symptom 5: `winpty` required for interactive commands

**What you see:** TTY-dependent prompts (e.g., `vais delete` confirmation) hang or display garbage in Git Bash.

**Fix:** Prefix with `winpty`:

```bash
winpty vais delete my-agent
```

Alternatively, pass `--yes` / `--force` to skip the interactive prompt on commands that support it.

---

## Quick-reference

| Symptom | Fix |
|---|---|
| `interpreter` not found | Use `.venv/Scripts/python.exe` in `plugin.yaml` on Windows |
| Volume mount path mangled | Prefix mount path with `//` or set `MSYS_NO_PATHCONV=1` |
| File path argument mangled | Use relative or Windows-native path; or set `MSYS_NO_PATHCONV=1` |
| Interactive prompt hangs | Prefix with `winpty` or add `--yes` flag |
| All of the above | Use PowerShell instead of Git Bash |

## See also

- [Package a Python plugin](package-a-python-plugin.md)
- [Install the runtime locally](install-the-runtime-locally.md)
