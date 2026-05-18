-- Copyright (c) 2026 VAIS contributors.
-- Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

-- CS-0: create eval result persistence tables.
-- Run this migration against your Postgres database before enabling PostgresEvalResultStore.
-- Safe to run multiple times (all statements use IF NOT EXISTS / ON CONFLICT).

CREATE TABLE IF NOT EXISTS vais_eval_runs (
    eval_run_id     TEXT        NOT NULL PRIMARY KEY,
    suite_name      TEXT        NOT NULL,
    suite_version   TEXT        NOT NULL DEFAULT '',
    started_at      TIMESTAMPTZ NOT NULL,
    completed_at    TIMESTAMPTZ,
    status          INT         NOT NULL DEFAULT 0,
    total_cases     INT         NOT NULL DEFAULT 0,
    passed_cases    INT         NOT NULL DEFAULT 0,
    failed_cases    INT         NOT NULL DEFAULT 0,
    source          TEXT        NOT NULL DEFAULT 'batch',
    window_start    TIMESTAMPTZ,
    window_end      TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_vais_eval_runs_suite_source
    ON vais_eval_runs (suite_name, source, started_at DESC);

CREATE TABLE IF NOT EXISTS vais_eval_case_results (
    id                  BIGSERIAL   NOT NULL PRIMARY KEY,
    eval_run_id         TEXT        NOT NULL REFERENCES vais_eval_runs (eval_run_id),
    case_id             TEXT        NOT NULL,
    agent_run_id        TEXT,
    started_at          TIMESTAMPTZ NOT NULL,
    completed_at        TIMESTAMPTZ,
    status              INT         NOT NULL DEFAULT 0,
    response_text       TEXT,
    assertion_results   JSONB       NOT NULL DEFAULT '[]',
    production_run_id   TEXT,
    CONSTRAINT uq_vais_eval_case_results UNIQUE (eval_run_id, case_id)
);

CREATE INDEX IF NOT EXISTS ix_vais_eval_case_results_run
    ON vais_eval_case_results (eval_run_id);
