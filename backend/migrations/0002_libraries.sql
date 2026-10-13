CREATE TABLE libraries (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name varchar(128) NOT NULL UNIQUE,
    media_type varchar(32) NOT NULL CHECK (media_type IN ('video', 'image', 'music', 'subtitles')),
    created_at timestamptz NOT NULL DEFAULT now()
);
