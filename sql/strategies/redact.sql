-- strategies/redact.sql
CREATE OR REPLACE FUNCTION anonymize.redact(seed TEXT, original TEXT)
RETURNS TEXT AS $$
DECLARE
    story_len BIGINT;
    desired_len INT;
    offset_val BIGINT;
    result TEXT := '';
BEGIN
    desired_len := length(original);
    SELECT length(content) INTO story_len FROM materials.story;

    -- Набираем подстроки рассказа до нужной длины
    WHILE length(result) < desired_len LOOP
        offset_val := utils.hmac_bigint(seed, result || original);
        result := result || substr(
            (SELECT content FROM materials.story),
            (abs(offset_val) % story_len)::INT + 1,
            desired_len - length(result)
        );
    END LOOP;
    RETURN result;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
