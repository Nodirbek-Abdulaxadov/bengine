import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { SchemaDocument } from '../schema/contract';
import { useDesigner } from '../state/store';
import * as client from '../api/client';
import { ImportSqlDialog } from './ImportSqlDialog';

vi.mock('../api/client');

const sampleSchema: SchemaDocument = {
  name: 'imported',
  tables: [
    {
      name: 'users',
      columns: [{ name: 'id', type: { dataType: 'BigInt' }, nullability: 'NotNull' }],
    },
  ],
};

beforeEach(() => {
  vi.resetAllMocks();
  useDesigner.getState().replaceAll({
    schemaName: 'shop',
    tables: [],
    foreignKeys: [],
    views: [],
    sequences: [],
    triggers: [],
  });
});

describe('ImportSqlDialog', () => {
  it('parses SQL via the backend and loads it onto the canvas', async () => {
    vi.mocked(client.importSql).mockResolvedValue({
      dialect: 'PostgreSql',
      detected: true,
      tableCount: 1,
      schema: sampleSchema,
    });
    const onClose = vi.fn();
    render(<ImportSqlDialog onClose={onClose} />);

    await userEvent.type(screen.getByRole('textbox'), 'CREATE TABLE users (id BIGINT PRIMARY KEY);');
    await userEvent.click(screen.getByRole('button', { name: /import qilish/i }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(client.importSql).toHaveBeenCalledWith(expect.stringContaining('CREATE TABLE users'), 'auto');
    expect(useDesigner.getState().tables.map((t) => t.name)).toEqual(['users']);
  });

  it('shows an error and does not call the backend when SQL is empty', async () => {
    const onClose = vi.fn();
    render(<ImportSqlDialog onClose={onClose} />);

    await userEvent.click(screen.getByRole('button', { name: /import qilish/i }));

    expect(await screen.findByText(/SQL matnini kiriting/i)).toBeInTheDocument();
    expect(client.importSql).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('passes the chosen dialect to the backend', async () => {
    vi.mocked(client.importSql).mockResolvedValue({
      dialect: 'MySql',
      detected: false,
      tableCount: 1,
      schema: sampleSchema,
    });
    render(<ImportSqlDialog onClose={vi.fn()} />);

    await userEvent.type(screen.getByRole('textbox'), 'CREATE TABLE t (id INT);');
    await userEvent.selectOptions(screen.getByRole('combobox'), 'MySql');
    await userEvent.click(screen.getByRole('button', { name: /import qilish/i }));

    await waitFor(() => expect(client.importSql).toHaveBeenCalledWith(expect.any(String), 'MySql'));
  });
});
