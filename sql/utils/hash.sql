-- utils/hash.sql
CREATE OR REPLACE FUNCTION utils.hmac_bigint(
    seed TEXT,
    input TEXT
) RETURNS BIGINT AS $$
DECLARE
    raw BYTEA;
    val BIGINT := 0;
    i INT;
BEGIN
    raw := hmac(input::BYTEA, seed::BYTEA, 'sha256');
    -- Первые 8 байт → BIGINT (без знака через bytea-арифметику)
    FOR i IN 0..7 LOOP
        val := (val << 8) | get_byte(raw, i)::BIGINT;
    END LOOP;
    RETURN val;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;

CREATE OR REPLACE FUNCTION utils.hmac_mod(
    seed TEXT,
    input TEXT,
    modulus BIGINT
) RETURNS BIGINT AS $$
BEGIN
    RETURN abs(utils.hmac_bigint(seed, input)) % modulus;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
