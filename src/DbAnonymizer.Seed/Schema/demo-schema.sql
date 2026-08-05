-- Таблицы и FK, максимально приближенные к реальной БД
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

CREATE TABLE users (
    id          SERIAL PRIMARY KEY,
    first_name  VARCHAR(100) NOT NULL,
    last_name   VARCHAR(100) NOT NULL,
    middle_name VARCHAR(100),
    email       VARCHAR(255) NOT NULL UNIQUE,
    phone       VARCHAR(20) NOT NULL,
    password_hash CHAR(60) NOT NULL,
    birth_date  DATE NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE profiles (
    id          SERIAL PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES users(id),
    address     TEXT NOT NULL,
    passport    VARCHAR(20),
    inn         VARCHAR(12),
    metadata    JSONB
);

CREATE TABLE orders (
    id          SERIAL PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES users(id),
    user_email  VARCHAR(255) NOT NULL,  -- денормализовано, FK по значению с users.email
    card_number VARCHAR(19) NOT NULL,
    amount      DECIMAL(10,2) NOT NULL,
    comment     TEXT,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX idx_orders_user_email ON orders(user_email);
CREATE INDEX idx_profiles_metadata ON profiles USING gin(metadata);
