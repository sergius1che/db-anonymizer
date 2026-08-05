-- strategies/keep.sql
CREATE OR REPLACE FUNCTION anonymize.keep(seed TEXT, original TEXT)
RETURNS TEXT AS $$
BEGIN
    RETURN original;
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
