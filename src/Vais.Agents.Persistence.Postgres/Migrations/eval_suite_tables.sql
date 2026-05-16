-- Copyright (c) 2026 VAIS contributors.
-- Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

-- Phase E2: create persistent eval suite storage tables.
-- This stub is a placeholder; tables are intentionally not created here.
-- E1 uses Orleans grain state for durability; Postgres persistence is Phase E2.

-- Placeholder for future migration:
--
-- CREATE TABLE IF NOT EXISTS vais_eval_suites (
--     id          TEXT    NOT NULL,
--     version     TEXT    NOT NULL,
--     manifest    JSONB   NOT NULL,
--     created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
--     PRIMARY KEY (id, version)
-- );
