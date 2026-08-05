-- strategies/shift.sql
CREATE OR REPLACE FUNCTION anonymize.shift(
    seed TEXT,
    original TEXT,  -- дата в ISO-формате (YYYY-MM-DD)
    shift_range_days INT
) RETURNS TEXT AS $$
DECLARE
    orig_date DATE;
    offset_val BIGINT;
    shifted DATE;
BEGIN
    orig_date := original::DATE;
    offset_val := utils.hmac_mod(seed, original, 2 * shift_range_days + 1) - shift_range_days;
    shifted := orig_date + offset_val * INTERVAL '1 day';
    RETURN to_char(shifted, 'YYYY-MM-DD');
END;
$$ LANGUAGE plpgsql IMMUTABLE STRICT PARALLEL SAFE;
