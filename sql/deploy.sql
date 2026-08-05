-- deploy.sql
CREATE SCHEMA IF NOT EXISTS utils;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE SCHEMA IF NOT EXISTS anonymize;

\ir utils/hash.sql
\ir utils/suffix.sql
\ir utils/text.sql
\ir strategies/keep.sql
\ir strategies/fake.sql
\ir strategies/format.sql
\ir strategies/secret.sql
\ir strategies/shift.sql
\ir strategies/redact.sql
\ir strategies/derive.sql
