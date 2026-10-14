ALTER TABLE execution_steps
  ADD COLUMN command_logs jsonb NOT NULL DEFAULT '[]'::jsonb;
