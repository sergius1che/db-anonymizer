-- strategies/format.sql
-- Перестановка домена для фиксированного алфавита
-- Материалы: materials.format_templates(type TEXT, template TEXT, format_mask TEXT,
-- digit_count INT) — несколько строк на type: original одного семантического типа
-- не всегда одной длины (напр. Visa — 13 или 16 цифр).
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
    mask_digit_count INT;
    multiplier BIGINT;
    coprime_last_digits INT[] := ARRAY[1, 3, 7, 9];
BEGIN
    -- Снимаем формат: оставляем только цифры
    cleaned := regexp_replace(original, '[^0-9]', '', 'g');

    -- 1. Точное совпадение по (type, digit_count) — маска подходит без подгонки
    SELECT format_mask INTO mask
    FROM materials.format_templates
    WHERE type = format_type AND digit_count = length(cleaned);

    IF mask IS NULL THEN
        -- 2. Нет варианта под точную длину — берём самый крупный сконфигурированный
        -- вариант этого типа как ориентир (используется и для проверки, что тип
        -- вообще есть в материалах)
        SELECT format_mask, digit_count INTO mask, mask_digit_count
        FROM materials.format_templates
        WHERE type = format_type
        ORDER BY digit_count DESC
        LIMIT 1;

        IF mask IS NULL THEN
            RETURN original; -- тип вообще не сконфигурирован — не трогаем
        END IF;

        IF length(cleaned) > mask_digit_count THEN
            -- Телефон "+7###-###-##-##" содержит цифру в самом префиксе — cleaned
            -- получает 11 цифр при маске на 10 нулей. Лишние ведущие цифры
            -- отбрасываем: иначе result выходит длиннее маски, и остаток
            -- дописывается как есть после неё, ломая длину и вид номера
            cleaned := right(cleaned, mask_digit_count);
        ELSE
            -- original короче любого сконфигурированного варианта (напр. Visa
            -- 13 цифр при сконфигурированных только под 16, или несвойственная
            -- длина ИНН) — под чужую маску не подгоняем: пермутация идёт без
            -- форматирования, но original всё равно не возвращается как есть
            mask := NULL;
        END IF;
    END IF;

    alphabet := '0123456789';
    -- NUMERIC: 10^16 точно представимо, в отличие от float8 (>2^53 ≈ 9e15)
    domain_size := power(length(alphabet)::NUMERIC, length(cleaned));

    -- Интерпретируем оригинал как число в base-|alphabet| → rank
    rank := 0;
    FOR i IN 1..length(cleaned) LOOP
        pos := position(substr(cleaned, i, 1) IN alphabet) - 1;
        rank := rank * length(alphabet) + pos;
    END LOOP;

    -- FPE: перестановка домена = (rank * multiplier + HMAC_offset) mod domain_size —
    -- affine, не чистый сдвиг Цезаря. Со сдвигом одна известная пара (original,
    -- anonymized) сразу давала offset = anonymized_rank - original_rank, что
    -- раскрывало ВСЕ значения этого типа без знания seed. Affine с неизвестным
    -- multiplier — система из одной пары недоопределена (2 неизвестных: a, b).
    -- multiplier обязан быть взаимно простым с domain_size (=10^L): domain_size
    -- делится только на 2 и 5, поэтому coprime достигается фиксацией последней
    -- десятичной цифры multiplier в {1,3,7,9} — тогда x → a*x mod n биекция
    -- (гарантирует уникальность анонимизированных значений, как и чистый сдвиг).
    multiplier := abs(utils.hmac_bigint(seed, format_type || ':multiplier'));
    multiplier := multiplier - multiplier % 10
        + coprime_last_digits[1 + (multiplier % 4)];

    -- numeric-арифметика: BIGINT-умножение/сложение может переполниться
    -- на 16-значных картах (>2^63-10^16); float8-модуль теряет точность выше 2^53.
    permuted_rank := ((rank::NUMERIC * multiplier::NUMERIC
                        + utils.hmac_bigint(seed, format_type)::NUMERIC)
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
    -- mask = NULL (длина не сконфигурирована) — result возвращается как есть,
    -- без форматирования, но original всё равно не раскрывается.
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
