-- strategies/fake.sql
-- Материалы: materials.pool_<component>(id BIGINT, value TEXT) — id 0-based (0..N-1),
-- совпадает с FakeMaterialBuilder (Task 4.1), который пишет (long)i для i=0..N-1
CREATE OR REPLACE FUNCTION anonymize.fake(
    seed TEXT,
    original TEXT,
    component TEXT,
    pool_size BIGINT,
    suffix_width INT
) RETURNS TEXT AS $$
DECLARE
    idx BIGINT;
    val TEXT;
    suf TEXT;
BEGIN
    -- pool_size приходит из замороженного плана (Plan.Pools), не из живого count(*):
    -- модуль хэширования должен быть стабилен между независимыми прогонами Enrich
    -- одного и того же плана, даже если LLM в следующий раз вернёт другой набор слов
    idx := utils.hmac_mod(seed, original, pool_size); -- 0-based индекс → id пула 0..N-1
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
