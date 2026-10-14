ALTER TABLE node_processing_settings
 ADD COLUMN schedule_enabled boolean NOT NULL DEFAULT false,
 ADD COLUMN schedule_timezone text NOT NULL DEFAULT 'UTC',
 ADD COLUMN schedule_slots text NOT NULL DEFAULT repeat('1',672) CHECK(length(schedule_slots)=672 AND schedule_slots !~ '[^01]');
