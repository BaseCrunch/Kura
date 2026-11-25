-- Kura schema v1. One database replaces the original app's ActiveAndroid + ObjectBox + SharedPreferences stores.
-- Post identity is (server_id, remote_id); md5 is a secondary key (SPEC 4.2 S6).

CREATE TABLE servers (
    id                      INTEGER PRIMARY KEY,
    name                    TEXT    NOT NULL,
    url                     TEXT    NOT NULL,
    engine                  INTEGER NOT NULL,
    username                TEXT,
    rating_filter_enabled   INTEGER NOT NULL DEFAULT 0,
    use_native_autocomplete INTEGER NOT NULL DEFAULT 1,
    is_default              INTEGER NOT NULL DEFAULT 0,
    sort_order              INTEGER NOT NULL DEFAULT 0,
    created_at              TEXT    NOT NULL
);
CREATE UNIQUE INDEX ux_servers_url ON servers(url COLLATE NOCASE);

-- Canonical post rows, shared by favorites, view history and downloads.
CREATE TABLE posts (
    id             INTEGER PRIMARY KEY,
    server_id      INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    remote_id      TEXT    NOT NULL,
    post_url       TEXT,
    md5            TEXT,
    rating         TEXT,
    score          INTEGER NOT NULL DEFAULT 0,
    source         TEXT,
    author         TEXT,
    parent_id      TEXT,
    has_children   INTEGER NOT NULL DEFAULT 0,
    has_notes      INTEGER NOT NULL DEFAULT 0,
    has_comments   INTEGER NOT NULL DEFAULT 0,
    created_at     TEXT,
    file_url       TEXT    NOT NULL,
    file_width     INTEGER NOT NULL DEFAULT 0,
    file_height    INTEGER NOT NULL DEFAULT 0,
    sample_url     TEXT,
    sample_width   INTEGER,
    sample_height  INTEGER,
    preview_url    TEXT,
    preview_width  INTEGER,
    preview_height INTEGER,
    ext            TEXT,
    updated_at     TEXT    NOT NULL
);
CREATE UNIQUE INDEX ux_posts_identity ON posts(server_id, remote_id);
CREATE INDEX ix_posts_md5 ON posts(md5);

CREATE TABLE tags (
    id       INTEGER PRIMARY KEY,
    name     TEXT    NOT NULL COLLATE NOCASE,
    category INTEGER NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX ux_tags_name ON tags(name);

CREATE TABLE post_tags (
    post_id INTEGER NOT NULL REFERENCES posts(id) ON DELETE CASCADE,
    tag_id  INTEGER NOT NULL REFERENCES tags(id)  ON DELETE CASCADE,
    PRIMARY KEY (post_id, tag_id)
) WITHOUT ROWID;
CREATE INDEX ix_post_tags_tag ON post_tags(tag_id, post_id);

-- Free-text search over local posts (tags + source); rowid = posts.id. Kept in sync by the repository layer.
CREATE VIRTUAL TABLE posts_fts USING fts5(
    tags, source,
    tokenize = "unicode61 tokenchars '_:-()'"
);

CREATE TABLE favorites (
    post_id  INTEGER PRIMARY KEY REFERENCES posts(id) ON DELETE CASCADE,
    added_at TEXT    NOT NULL,
    synced   INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_favorites_added ON favorites(added_at);

CREATE TABLE view_history (
    post_id   INTEGER PRIMARY KEY REFERENCES posts(id) ON DELETE CASCADE,
    viewed_at TEXT NOT NULL
);
CREATE INDEX ix_view_history_viewed ON view_history(viewed_at);

CREATE TABLE search_history (
    id          INTEGER PRIMARY KEY,
    server_id   INTEGER REFERENCES servers(id) ON DELETE SET NULL,
    query       TEXT    NOT NULL,
    query_lower TEXT    NOT NULL,
    searched_at TEXT    NOT NULL,
    starred     INTEGER NOT NULL DEFAULT 0,
    use_count   INTEGER NOT NULL DEFAULT 1
);
CREATE UNIQUE INDEX ux_search_history_query ON search_history(query_lower);
CREATE INDEX ix_search_history_date ON search_history(starred DESC, searched_at DESC);

CREATE TABLE blacklist_rules (
    id         INTEGER PRIMARY KEY,
    rule       TEXT    NOT NULL,
    sites      TEXT,               -- comma-separated host scope, NULL = all servers
    enabled    INTEGER NOT NULL DEFAULT 1,
    created_at TEXT    NOT NULL
);
CREATE UNIQUE INDEX ux_blacklist_rule ON blacklist_rules(rule, IFNULL(sites, ''));

-- Home pins / saved sources; nested via parent_id (SourceSpecs groups).
CREATE TABLE pins (
    id         INTEGER PRIMARY KEY,
    parent_id  INTEGER REFERENCES pins(id) ON DELETE CASCADE,
    kind       INTEGER NOT NULL,   -- 0 none, 1 favorites, 2 history, 3 search, 4 group
    title      TEXT,
    server_id  INTEGER REFERENCES servers(id) ON DELETE CASCADE,
    query_json TEXT,
    sort_order INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE pool_favorites (
    id          INTEGER PRIMARY KEY,
    server_id   INTEGER NOT NULL REFERENCES servers(id) ON DELETE CASCADE,
    remote_id   TEXT    NOT NULL,
    name        TEXT    NOT NULL,
    description TEXT,
    url         TEXT,
    added_at    TEXT    NOT NULL
);
CREATE UNIQUE INDEX ux_pool_favorites ON pool_favorites(server_id, remote_id);

CREATE TABLE downloads (
    id            INTEGER PRIMARY KEY,
    post_id       INTEGER REFERENCES posts(id) ON DELETE SET NULL,
    url           TEXT    NOT NULL,
    referer       TEXT,
    target_path   TEXT    NOT NULL,
    status        INTEGER NOT NULL DEFAULT 0, -- 0 waiting, 1 running, 2 done, 3 failed, 4 duplicate, 5 paused
    bytes_total   INTEGER,
    bytes_done    INTEGER NOT NULL DEFAULT 0,
    content_hash  TEXT,
    error_message TEXT,
    query         TEXT,
    created_at    TEXT    NOT NULL,
    finished_at   TEXT
);
CREATE INDEX ix_downloads_status ON downloads(status, id);

-- Tag-name -> category cache fed by engines that return categorised tags (Danbooru 2, e621, autocomplete).
CREATE TABLE tag_cache (
    name       TEXT    PRIMARY KEY COLLATE NOCASE,
    category   INTEGER NOT NULL,
    post_count INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT    NOT NULL
) WITHOUT ROWID;

CREATE TABLE settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
) WITHOUT ROWID;
