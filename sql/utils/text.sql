-- utils/text.sql
-- word_by_length(word_len INT, pos BIGINT) → слово нужной длины из рассказа по позиции
-- substring_from_story(seed TEXT, input TEXT, desired_len INT) → подстрока рассказа
-- Используется стратегией Redact. Материалы создаются в Task 4.2:
--   materials.story(content TEXT) — одна строка, весь рассказ
--   materials.story_words(len INT, word TEXT, idx BIGINT) — индекс слов по длине

CREATE OR REPLACE FUNCTION utils.word_by_length(word_len INT, pos BIGINT)
RETURNS TEXT AS $$
DECLARE
    total BIGINT;
    result TEXT;
BEGIN
    SELECT count(*) INTO total FROM materials.story_words WHERE len = word_len;
    IF total = 0 THEN
        RETURN NULL;
    END IF;
    SELECT word INTO result
    FROM materials.story_words
    WHERE len = word_len
    ORDER BY idx
    LIMIT 1 OFFSET (abs(pos) % total);
    RETURN result;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;

CREATE OR REPLACE FUNCTION utils.substring_from_story(seed TEXT, input TEXT, desired_len INT)
RETURNS TEXT AS $$
DECLARE
    story_len BIGINT;
    offset_val BIGINT;
BEGIN
    SELECT length(content) INTO story_len FROM materials.story;
    offset_val := abs(utils.hmac_bigint(seed, input)) % story_len;
    RETURN substr((SELECT content FROM materials.story), offset_val::INT + 1, desired_len);
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
