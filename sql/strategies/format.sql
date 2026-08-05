-- strategies/format.sql
-- Перестановка домена для фиксированного алфавита
-- Материалы: materials.format_template(type TEXT, template TEXT, format_mask TEXT)
CREATE OR REPLACE FUNCTION anonymize.format(
    seed TEXT,
    original TEXT,
    format_type TEXT,
    suffix_width INT DEFAULT 0
) RETURNS TEXT AS $$
DECLARE
    cleaned TEXT;       -- оригинал после снятия форматирования (одни цифры)
    alphabet TEXT;
    domain_size NUMERIC; -- NUMERIC: power(10,16) теряет точность в float8 (>2^53)
    rank BIGINT;
    permuted_rank BIGINT;
    result TEXT;
    mask TEXT;
    i INT;
    pos INT;
    template_str TEXT;
BEGIN
    -- Извлекаем шаблон формата и маску
    SELECT template, format_mask INTO template_str, mask
    FROM materials.format_templates WHERE type = format_type;
    IF NOT FOUND THEN
        RETURN original; -- нет шаблона — не трогаем
    END IF;

    -- Снимаем формат: оставляем только цифры
    cleaned := regexp_replace(original, '[^0-9]', '', 'g');
    alphabet := '0123456789';
    -- NUMERIC: 10^16 точно представимо, в отличие от float8 (>2^53 ≈ 9e15)
    domain_size := power(length(alphabet)::NUMERIC, length(cleaned));

    -- Интерпретируем оригинал как число в base-|alphabet| → rank
    rank := 0;
    FOR i IN 1..length(cleaned) LOOP
        pos := position(substr(cleaned, i, 1) IN alphabet) - 1;
        rank := rank * length(alphabet) + pos;
    END LOOP;

    -- FPE: перестановка домена = (rank + HMAC_offset) mod domain_size.
    -- numeric-арифметика: BIGINT-сложение (rank + hmac_bigint) может переполниться
    -- на 16-значных картах (>2^63-10^16); float8-модуль теряет точность выше 2^53.
    permuted_rank := ((rank::NUMERIC + utils.hmac_bigint(seed, format_type)::NUMERIC)
                      % domain_size)::BIGINT;
    IF permuted_rank < 0 THEN
        permuted_rank := permuted_rank + domain_size::BIGINT;
    END IF;

    -- Превращаем permuted_rank обратно в строку над алфавитом
    result := '';
    FOR i IN 1..length(cleaned) LOOP
        result := substr(alphabet, (permuted_rank % length(alphabet))::INT + 1, 1) || result;
        permuted_rank := permuted_rank / length(alphabet);
    END LOOP;

    -- Восстанавливаем формат по маске: каждый '0' заменяется следующей цифрой результата.
    -- Маска для телефона: "+7 000 000-00-00", для карты: "0000 0000 0000 0000".
    -- Символы маски, отличные от '0', переносятся как есть (пробелы, +, дефисы).
    IF mask IS NOT NULL THEN
        DECLARE
            digit_idx INT := 1;
            mask_char TEXT;
            formatted TEXT := '';
        BEGIN
            FOR i IN 1..length(mask) LOOP
                mask_char := substr(mask, i, 1);
                IF mask_char = '0' THEN
                    formatted := formatted || substr(result, digit_idx, 1);
                    digit_idx := digit_idx + 1;
                ELSE
                    formatted := formatted || mask_char;
                END IF;
            END LOOP;
            -- Если в маске меньше нулей, чем цифр, дописываем оставшиеся
            IF digit_idx <= length(result) THEN
                formatted := formatted || substr(result, digit_idx);
            END IF;
            result := formatted;
        END;
    END IF;

    RETURN result;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
