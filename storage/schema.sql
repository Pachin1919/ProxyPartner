-- Design contract, validated with SQLite in this kit, not a completed app migration system.
PRAGMA foreign_keys=ON;
PRAGMA auto_vacuum=INCREMENTAL;
PRAGMA cache_size=-2048;
PRAGMA mmap_size=0;
PRAGMA busy_timeout=1000;
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
CREATE TABLE schema_version(version INTEGER PRIMARY KEY, applied_at_ms INTEGER NOT NULL) STRICT;
INSERT INTO schema_version VALUES(1,0);
CREATE TABLE sources(id TEXT PRIMARY KEY, host_kind TEXT NOT NULL, epoch INTEGER NOT NULL CHECK(epoch>=0), created_at_ms INTEGER NOT NULL) STRICT;
CREATE TABLE nodes(id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES sources(id), native_name TEXT NOT NULL, provider_key TEXT NOT NULL, revision TEXT NOT NULL, is_group INTEGER NOT NULL CHECK(is_group IN(0,1)), updated_at_ms INTEGER NOT NULL) STRICT;
CREATE TABLE targets(id TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision>0), safe_url TEXT NOT NULL, profile TEXT NOT NULL, PRIMARY KEY(id,revision)) STRICT;
CREATE TABLE samples(
 sample_id TEXT PRIMARY KEY, dedup_key BLOB NOT NULL UNIQUE CHECK(length(dedup_key)=32),
 source_id TEXT NOT NULL REFERENCES sources(id), source_epoch INTEGER NOT NULL,
 node_id TEXT NOT NULL REFERENCES nodes(id), node_revision TEXT NOT NULL,
 target_id TEXT NOT NULL, target_revision INTEGER NOT NULL,
 context_id TEXT NOT NULL, observed_at_ms INTEGER NOT NULL, collected_at_ms INTEGER NOT NULL,
 source_kind TEXT NOT NULL CHECK(source_kind IN('native_history','isolated_probe','current_route_probe')),
 measurement_kind TEXT NOT NULL CHECK(measurement_kind IN('native_http_delay','cold_http_ttfb')),
 outcome TEXT NOT NULL CHECK(outcome IN('ok','timeout','dns_error','connect_error','tls_error','read_error','unknown_native_failure','cancelled','source_unavailable','resource_limit')),
 latency_ms INTEGER CHECK(latency_ms IS NULL OR latency_ms BETWEEN 0 AND 300000),
 http_status INTEGER CHECK(http_status IS NULL OR http_status BETWEEN 100 AND 599),
 service_state TEXT NOT NULL CHECK(service_state IN('unverified','blocked_or_challenged','rate_limited','server_error','not_applicable')),
 route_state TEXT NOT NULL CHECK(route_state IN('verified','route_unverified','current_route','native_reported')),
 CHECK(outcome!='ok' OR latency_ms IS NOT NULL),
 FOREIGN KEY(target_id,target_revision) REFERENCES targets(id,revision)
) STRICT;
CREATE INDEX ix_sample_window ON samples(node_id,node_revision,target_id,measurement_kind,context_id,observed_at_ms);
CREATE INDEX ix_sample_retention ON samples(observed_at_ms);
CREATE TABLE aggregates(
 grain TEXT NOT NULL CHECK(grain IN('hour','day')), bucket_at_ms INTEGER NOT NULL,
 node_id TEXT NOT NULL REFERENCES nodes(id), node_revision TEXT NOT NULL,
 target_id TEXT NOT NULL, target_revision INTEGER NOT NULL,
 context_id TEXT NOT NULL, source_kind TEXT NOT NULL, measurement_kind TEXT NOT NULL,
 attempted INTEGER NOT NULL CHECK(attempted>=0), succeeded INTEGER NOT NULL CHECK(succeeded BETWEEN 0 AND attempted),
 mean_ms REAL, m2_ms2 REAL CHECK(m2_ms2 IS NULL OR m2_ms2>=0),
 PRIMARY KEY(grain,bucket_at_ms,node_id,node_revision,target_id,target_revision,context_id,source_kind,measurement_kind),
 FOREIGN KEY(target_id,target_revision) REFERENCES targets(id,revision)
) STRICT;
CREATE TABLE ip_observations(
 id TEXT PRIMARY KEY, node_id TEXT NOT NULL REFERENCES nodes(id), node_revision TEXT NOT NULL,
 source_epoch INTEGER NOT NULL, context_id TEXT NOT NULL, endpoint_id TEXT NOT NULL,
 family INTEGER NOT NULL CHECK(family IN(4,6)), ip BLOB NOT NULL,
 observed_at_ms INTEGER NOT NULL, route_state TEXT NOT NULL,
 CHECK((family=4 AND length(ip)=4) OR (family=6 AND length(ip)=16))
) STRICT;
CREATE INDEX ix_ip_window ON ip_observations(node_id,node_revision,context_id,endpoint_id,family,observed_at_ms);
CREATE TABLE ip_changes(
 id TEXT PRIMARY KEY, node_id TEXT NOT NULL REFERENCES nodes(id), node_revision TEXT NOT NULL,
 context_id TEXT NOT NULL, endpoint_id TEXT NOT NULL, family INTEGER NOT NULL CHECK(family IN(4,6)),
 previous_at_ms INTEGER NOT NULL, observed_at_ms INTEGER NOT NULL,
 previous_ip BLOB NOT NULL, observed_ip BLOB NOT NULL,
 CHECK(observed_at_ms>previous_at_ms)
) STRICT;
CREATE TABLE incidents(
 id TEXT PRIMARY KEY, node_id TEXT NOT NULL REFERENCES nodes(id), target_id TEXT NOT NULL, target_revision INTEGER NOT NULL,
 method TEXT NOT NULL, context_id TEXT NOT NULL,
 last_success_ms INTEGER, first_failure_ms INTEGER NOT NULL,
 confirmed_at_ms INTEGER NOT NULL, last_failure_ms INTEGER NOT NULL,
 first_recovery_ms INTEGER, closed_at_ms INTEGER,
 state TEXT NOT NULL CHECK(state IN('confirmed','recovered','censored')),
 FOREIGN KEY(target_id,target_revision) REFERENCES targets(id,revision)
) STRICT;
CREATE TABLE observation_gaps(id TEXT PRIMARY KEY, source_id TEXT NOT NULL REFERENCES sources(id), start_ms INTEGER NOT NULL, end_ms INTEGER, reason TEXT NOT NULL CHECK(reason IN('sleep','observer_stopped','source_unavailable','resource_limit','local_network_change'))) STRICT;
CREATE TABLE retention_watermarks(scope TEXT PRIMARY KEY, aggregated_through_ms INTEGER NOT NULL, deleted_before_ms INTEGER NOT NULL) STRICT;
CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
