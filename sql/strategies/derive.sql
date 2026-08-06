-- strategies/derive.sql
-- first_name_fake/last_name_fake приходят уже посчитанными вызывающей стороной
-- (anonymize.fake от РЕАЛЬНЫХ first_name/last_name колонки строки, не от original
-- этой колонки) — иначе фейковое имя в email не совпадало бы с тем, что показывают
-- Fake(FirstName)/Fake(LastName) колонки той же строки
CREATE OR REPLACE FUNCTION anonymize.derive(
    seed TEXT,
    original TEXT,
    first_name_fake TEXT,
    last_name_fake TEXT,
    domain TEXT,          -- '@mail.invalid'
    suffix_width INT
) RETURNS TEXT AS $$
DECLARE
    local_part TEXT;
    suf TEXT;
BEGIN
    -- Локальная часть: first.last из уже фейковых имён; В MVP транслитерация упрощённая
    local_part := lower(regexp_replace(
        first_name_fake || '.' || last_name_fake,
        '\s+', '.', 'g'
    ));
    suf := utils.base36(seed, original, suffix_width);
    RETURN local_part || '.' || suf || domain;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
