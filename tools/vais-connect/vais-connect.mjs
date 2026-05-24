#!/usr/bin/env node
// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { homedir } from 'node:os';
import { parseArgs } from 'node:util';

const VERSION = '0.1.0';
const DESIGN_PATH = '/design-mcp';

const HELP = `\
@vais/connect ${VERSION}
Write MCP connection config for the Vais design-tools server.

Usage:
  npx @vais/connect [options]          detect agent and write config
  npx @vais/connect update [options]   re-write the vais-design entry

Options:
  --url     runtime base URL (default: http://localhost:5000)
  --token   bearer token (required when VAIS_JWT_AUTHORITY is set)
  --agent   force agent: claude-code | opencode | codex | generic  (default: auto)
  --name    MCP server name written into the config (default: vais-design)
  -h, --help

Agent detection order (auto):
  1. CLAUDE_CODE_ENTRYPOINT env var set    → claude-code
  2. opencode.json exists in cwd           → opencode
  3. CODEX_WORKSPACE env var set           → codex
  4. default                               → claude-code

Config written:
  claude-code  .mcp.json           (merged into project root)
  opencode     opencode.json       (merged into project root)
  codex        ~/.codex/config.toml (TOML fragment appended or printed to stdout)
  generic      stdout only         (print config snippet for manual paste)
`;

const { values: opts, positionals } = parseArgs({
  options: {
    url:   { type: 'string', default: 'http://localhost:5000' },
    token: { type: 'string', default: '' },
    agent: { type: 'string', default: 'auto' },
    name:  { type: 'string', default: 'vais-design' },
    help:  { type: 'boolean', short: 'h', default: false },
  },
  allowPositionals: true,
  strict: false,
});

if (opts.help) { process.stdout.write(HELP); process.exit(0); }

const sub = positionals[0] ?? 'connect';
if (sub !== 'connect' && sub !== 'update') {
  console.error(`error: unknown subcommand '${sub}'. Use: connect | update`);
  process.exit(1);
}

const baseUrl = opts.url.replace(/\/$/, '');
const mcpUrl  = baseUrl + DESIGN_PATH;
const name    = opts.name;
const headers = opts.token ? { Authorization: `Bearer ${opts.token}` } : {};
const noToken = !opts.token;

const agent = opts.agent === 'auto' ? detectAgent() : opts.agent;

switch (agent) {
  case 'claude-code': writeClaudeCode(); break;
  case 'opencode':    writeOpenCode();   break;
  case 'codex':       writeCodex();      break;
  case 'generic':     printGeneric();    break;
  default:
    console.error(`error: unknown agent '${agent}'. Use: claude-code | opencode | codex | generic`);
    process.exit(1);
}

// ── Agent detection ───────────────────────────────────────────────────────────

function detectAgent() {
  if (process.env.CLAUDE_CODE_ENTRYPOINT) return 'claude-code';
  if (existsSync(resolve(process.cwd(), 'opencode.json')))  return 'opencode';
  if (process.env.CODEX_WORKSPACE || process.env.CODEX_SANDBOX_NETWORK) return 'codex';
  return 'claude-code';
}

// ── Claude Code (.mcp.json) ───────────────────────────────────────────────────

function writeClaudeCode() {
  const filePath = resolve(process.cwd(), '.mcp.json');
  mergeJson(filePath, (existing) => {
    existing.mcpServers ??= {};
    existing.mcpServers[name] = buildHttpEntry();
    return existing;
  });
  console.log(`[ok] Written: ${filePath}`);
  console.log(`     Server '${name}' -> ${mcpUrl}`);
  tokenHint();
}

// ── OpenCode (opencode.json) ──────────────────────────────────────────────────

function writeOpenCode() {
  const filePath = resolve(process.cwd(), 'opencode.json');
  mergeJson(filePath, (existing) => {
    existing.mcp ??= {};
    existing.mcp[name] = { type: 'remote', url: mcpUrl, ...headersObj() };
    return existing;
  });
  console.log(`[ok] Written: ${filePath}`);
  console.log(`     Server '${name}' -> ${mcpUrl}`);
  tokenHint();
}

// ── Codex (~/.codex/config.toml) ─────────────────────────────────────────────
// Codex does not have a stable public JSON MCP config format. We generate a
// TOML fragment to append into ~/.codex/config.toml (created if absent).

function writeCodex() {
  const configDir = join(homedir(), '.codex');
  const filePath  = join(configDir, 'config.toml');

  const headerLines = opts.token
    ? `  [mcpServers.${name}.headers]\n  Authorization = "Bearer ${opts.token}"\n`
    : '';

  const fragment = `\n# Added by @vais/connect ${VERSION}\n[mcpServers.${name}]\nurl = "${mcpUrl}"\n${headerLines}`;

  let existing = '';
  if (existsSync(filePath)) existing = readFileSync(filePath, 'utf8');

  // If the entry is already present, print instructions instead of double-appending
  if (existing.includes(`[mcpServers.${name}]`)) {
    console.log(`[info] ${filePath} already contains [mcpServers.${name}].`);
    console.log(`       Run with --name <other> to add a differently-named entry, or edit manually.`);
    return;
  }

  try {
    if (!existsSync(configDir)) {
      mkdirSync(configDir, { recursive: true });
    }
    writeFileSync(filePath, existing + fragment, 'utf8');
    console.log(`[ok] Appended to: ${filePath}`);
    console.log(`     Server '${name}' -> ${mcpUrl}`);
    tokenHint();
  } catch {
    console.log(`[info] Could not write ${filePath}. Add this fragment manually:\n`);
    process.stdout.write(fragment + '\n');
  }
}

// ── Generic (stdout) ──────────────────────────────────────────────────────────

function printGeneric() {
  console.log(`MCP server name : ${name}`);
  console.log(`Endpoint URL    : ${mcpUrl}`);
  if (opts.token) console.log(`Authorization   : Bearer ${opts.token}`);
  console.log('');
  console.log('Claude Code (.mcp.json entry):');
  console.log(JSON.stringify({ mcpServers: { [name]: buildHttpEntry() } }, null, 2));
  tokenHint();
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function buildHttpEntry() {
  return { type: 'http', url: mcpUrl, ...headersObj() };
}

function headersObj() {
  return Object.keys(headers).length ? { headers } : {};
}

function mergeJson(filePath, transform) {
  let existing = {};
  if (existsSync(filePath)) {
    try { existing = JSON.parse(readFileSync(filePath, 'utf8')); }
    catch { /* start fresh on parse error */ }
  }
  writeFileSync(filePath, JSON.stringify(transform(existing), null, 2) + '\n', 'utf8');
}

function tokenHint() {
  if (noToken) {
    console.log('');
    console.log('[note] No --token provided. This is fine when the runtime runs without JWT auth');
    console.log('       (VAIS_JWT_AUTHORITY unset). Supply --token <bearer-token> otherwise.');
  }
}
