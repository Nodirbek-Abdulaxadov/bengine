-- PostgreSQL dialect sample
CREATE TABLE public.users (
    id BIGSERIAL PRIMARY KEY,
    email VARCHAR(320) NOT NULL,
    full_name VARCHAR(200) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT true,
    balance NUMERIC(12,2) NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    metadata JSONB
);

CREATE UNIQUE INDEX ux_users_email ON public.users (email);

CREATE TABLE public.orders (
    id BIGSERIAL PRIMARY KEY,
    user_id BIGINT NOT NULL REFERENCES public.users (id) ON DELETE CASCADE,
    total NUMERIC(12,2) NOT NULL DEFAULT 0,
    placed_at TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT current_timestamp,
    CONSTRAINT ck_orders_total CHECK (total >= 0)
);

CREATE INDEX ix_orders_user ON public.orders (user_id);
