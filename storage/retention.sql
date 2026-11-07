-- Run only after hourly aggregation has committed and matching watermarks advanced.
-- Bind :cutoff_ms and :aggregated_through_ms using parameters; max 500 per transaction.
BEGIN IMMEDIATE;
DELETE FROM samples WHERE sample_id IN (
 SELECT sample_id FROM samples
 WHERE observed_at_ms<:cutoff_ms AND observed_at_ms<=:aggregated_through_ms
 ORDER BY observed_at_ms LIMIT 500
);
COMMIT;
-- During idle only; if busy, back off. This does not guarantee immediate file shrink.
PRAGMA wal_checkpoint(PASSIVE);
PRAGMA incremental_vacuum(128);
