-- strategies/secret.sql
CREATE OR REPLACE FUNCTION anonymize.secret(seed TEXT, original TEXT)
RETURNS TEXT AS $$
BEGIN
    -- Замена на хэш известного dev-пароля; детерминированно по seed+original
    RETURN encode(hmac(
        'dev-password'::BYTEA,
        (seed || original)::BYTEA,
        'sha256'
    ), 'hex');
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
