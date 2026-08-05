-- utils/suffix.sql
CREATE OR REPLACE FUNCTION utils.base36(seed TEXT, input TEXT, width INT)
RETURNS TEXT AS $$
DECLARE
    chars TEXT := '0123456789abcdefghijklmnopqrstuvwxyz';
    val BIGINT := utils.hmac_bigint(seed, input);
    result TEXT := '';
    i INT;
BEGIN
    FOR i IN 1..width LOOP
        result := substr(chars, (abs(val) % 36)::INT + 1, 1) || result;
        val := val / 36;
    END LOOP;
    RETURN result;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
