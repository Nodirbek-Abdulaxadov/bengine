import { describe, expect, it } from 'vitest';
import type { SchemaDocument } from '../schema/contract';
import { fromSchemaDocument, toSchemaDocument } from './mapping';

const doc: SchemaDocument = {
  name: 'shop',
  tables: [
    {
      name: 'users',
      schema: 'public',
      columns: [
        { name: 'id', type: { dataType: 'BigInt' }, nullability: 'NotNull', identity: { strategy: 'Identity' } },
        {
          name: 'email',
          type: { dataType: 'VarChar', length: 320 },
          nullability: 'NotNull',
          validation: { kind: 'String', string: { minLength: 3, maxLength: 320 } },
        },
        { name: 'bio', type: { dataType: 'Text' }, nullability: 'Nullable' },
      ],
      primaryKey: { name: 'pk_users', columnNames: ['id'] },
      indexes: [{ name: 'ux_users_email', type: 'Unique', columns: [{ columnName: 'email' }] }],
    },
    {
      name: 'orders',
      schema: 'public',
      columns: [
        { name: 'id', type: { dataType: 'BigInt' }, nullability: 'NotNull', identity: { strategy: 'Identity' } },
        { name: 'user_id', type: { dataType: 'BigInt' }, nullability: 'NotNull' },
        {
          name: 'total',
          type: { dataType: 'Decimal', precision: 12, scale: 2 },
          nullability: 'NotNull',
          default: { kind: 'Literal', value: '0' },
        },
      ],
      primaryKey: { name: 'pk_orders', columnNames: ['id'] },
      foreignKeys: [
        {
          name: 'fk_orders_user',
          columns: ['user_id'],
          referencedTable: 'users',
          referencedSchema: 'public',
          referencedColumns: ['id'],
          onDelete: 'Cascade',
          onUpdate: 'NoAction',
        },
      ],
      checkConstraints: [{ name: 'ck_orders_total', expression: 'total >= 0' }],
    },
  ],
};

describe('schema document mapping', () => {
  it('loads a document into editor state', () => {
    const snapshot = fromSchemaDocument(doc);

    expect(snapshot.schemaName).toBe('shop');
    expect(snapshot.tables.map((t) => t.name)).toEqual(['users', 'orders']);

    const email = snapshot.tables[0].columns.find((c) => c.name === 'email');
    expect(email?.length).toBe(320);
    expect(email?.nullable).toBe(false);

    const bio = snapshot.tables[0].columns.find((c) => c.name === 'bio');
    expect(bio?.nullable).toBe(true);

    expect(snapshot.foreignKeys).toHaveLength(1);
    expect(snapshot.foreignKeys[0].onDelete).toBe('Cascade');
  });

  it('round-trips document -> editor state -> document', () => {
    const restored = toSchemaDocument(fromSchemaDocument(doc));

    expect(restored.name).toBe(doc.name);
    expect(restored.tables).toHaveLength(2);

    const orders = restored.tables.find((t) => t.name === 'orders')!;
    expect(orders.columns.map((c) => c.name)).toEqual(['id', 'user_id', 'total']);
    expect(orders.primaryKey?.columnNames).toEqual(['id']);
    expect(orders.foreignKeys?.[0]).toMatchObject({
      columns: ['user_id'],
      referencedTable: 'users',
      referencedColumns: ['id'],
      onDelete: 'Cascade',
    });
    expect(orders.checkConstraints?.[0].expression).toBe('total >= 0');

    const total = orders.columns.find((c) => c.name === 'total')!;
    expect(total.type).toMatchObject({ dataType: 'Decimal', precision: 12, scale: 2 });
    expect(total.default).toMatchObject({ kind: 'Literal', value: '0' });

    const users = restored.tables.find((t) => t.name === 'users')!;
    expect(users.indexes?.[0]).toMatchObject({ name: 'ux_users_email', type: 'Unique' });
  });
});
