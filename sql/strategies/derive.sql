-- strategies/derive.sql
CREATE OR REPLACE FUNCTION anonymize.derive(
    seed TEXT,
    original TEXT,
    domain TEXT,          -- '@mail.invalid'
    suffix_width INT
) RETURNS TEXT AS $$
DECLARE
    local_part TEXT;
    suf TEXT;
BEGIN
    -- Локальная часть: first.last (берётся из фейкового имени — аналог anonymize.fake)
    -- В MVP: транслитерация упрощённая, вызывает fake для компонент и склеивает
    local_part := lower(regexp_replace(
        anonymize.fake(seed, original, 'firstname', 0) || '.' ||
        anonymize.fake(seed, original, 'lastname', 0),
        '\s+', '.', 'g'
    ));
    suf := utils.base36(seed, original, suffix_width);
    RETURN local_part || '.' || suf || domain;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
