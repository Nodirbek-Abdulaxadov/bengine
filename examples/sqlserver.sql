-- SQL Server dialect sample
CREATE TABLE [dbo].[customers] (
    [id] BIGINT IDENTITY(1,1) NOT NULL,
    [email] NVARCHAR(320) NOT NULL,
    [full_name] NVARCHAR(200) NOT NULL,
    [notes] VARCHAR(MAX) NULL,
    [external_id] UNIQUEIDENTIFIER NULL,
    [created_at] DATETIME2 NOT NULL DEFAULT GETDATE(),
    CONSTRAINT [pk_customers] PRIMARY KEY CLUSTERED ([id])
);

CREATE UNIQUE INDEX [ux_customers_email] ON [dbo].[customers] ([email]);

CREATE TABLE [dbo].[invoices] (
    [id] BIGINT IDENTITY(1,1) NOT NULL,
    [customer_id] BIGINT NOT NULL,
    [amount] DECIMAL(12,2) NOT NULL,
    [is_paid] BIT NOT NULL DEFAULT 0,
    CONSTRAINT [pk_invoices] PRIMARY KEY ([id]),
    CONSTRAINT [fk_invoices_customer] FOREIGN KEY ([customer_id]) REFERENCES [dbo].[customers] ([id]) ON DELETE CASCADE
);
