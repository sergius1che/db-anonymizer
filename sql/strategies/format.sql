-- strategies/format.sql
-- Перестановка домена для фиксированного алфавита
-- Материалы: materials.format_template(type TEXT, template TEXT) — regex + замена
CREATE OR REPLACE FUNCTION anonymize.format(
    seed TEXT,
    original TEXT,
    format_type TEXT,
    suffix_width INT DEFAULT 0
) RETURNS TEXT AS $$
DECLARE
    cleaned TEXT;    -- оригинал после снятия форматирования (одни цифры)
    alphabet TEXT;
    domain_size BIGINT;
    rank BIGINT;
    permuted_rank BIGINT;
    result TEXT;
    i INT;
    pos INT;
    template_str TEXT;
BEGIN
    -- Извлекаем шаблон формата (в плане: телефон → \d{10}, карта → \d{16})
    SELECT template INTO template_str
    FROM materials.format_templates WHERE type = format_type;
    IF NOT FOUND THEN
        RETURN original; -- нет шаблона — не трогаем
    END IF;

    -- Снимаем формат: оставляем только цифры
    cleaned := regexp_replace(original, '[^0-9]', '', 'g');
    alphabet := '0123456789';
    domain_size := power(length(alphabet)::BIGINT, length(cleaned)::INT);

    -- Интерпретируем оригинал как число в base-|alphabet| → rank
    rank := 0;
    FOR i IN 1..length(cleaned) LOOP
        pos := position(substr(cleaned, i, 1) IN alphabet) - 1;
        rank := rank * length(alphabet) + pos;
    END LOOP;

    -- FPE: перестановка домена = (rank + HMAC_offset) mod domain_size
    -- offset привязан к домену (format_type), а не к конкретному входу —
    -- иначе у каждого входа своя константа и отображение перестаёт быть биекцией
    permuted_rank := ((rank + utils.hmac_bigint(seed, format_type)) % domain_size + domain_size) % domain_size;

    -- Превращаем permuted_rank обратно в строку над алфавитом
    result := '';
    FOR i IN 1..length(cleaned) LOOP
        result := substr(alphabet, (permuted_rank % length(alphabet))::INT + 1, 1) || result;
        permuted_rank := permuted_rank / length(alphabet);
    END LOOP;

    -- Восстанавливаем формат по шаблону (template_str описывает маску)
    -- Для MVP: просто возвращаем цифры как есть, формат восстанавливается в .NET
    RETURN result;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
