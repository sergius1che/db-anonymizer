-- strategies/fake.sql
-- Материалы: materials.pool_<component>(id BIGINT, value TEXT) — id 0-based (0..N-1),
-- совпадает с FakeMaterialBuilder (Task 4.1), который пишет (long)i для i=0..N-1
CREATE OR REPLACE FUNCTION anonymize.fake(
    seed TEXT,
    original TEXT,
    component TEXT,
    suffix_width INT
) RETURNS TEXT AS $$
DECLARE
    idx BIGINT;
    val TEXT;
    suf TEXT;
BEGIN
    EXECUTE format(
        'SELECT (SELECT count(*) FROM materials.pool_%I)::BIGINT', component
    ) INTO idx; -- размер пула, дальше используем как modulus
    idx := utils.hmac_mod(seed, original, idx); -- 0-based индекс → id пула 0..N-1
    EXECUTE format(
        'SELECT value FROM materials.pool_%I WHERE id = $1', component
    ) INTO val USING idx;
    IF suffix_width > 0 THEN
        suf := utils.base36(seed, original, suffix_width);
        RETURN val || ' ' || suf;
    END IF;
    RETURN val;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
