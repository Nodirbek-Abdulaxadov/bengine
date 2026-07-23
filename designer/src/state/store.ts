import { create } from 'zustand';
import type {
  Charset,
  Collation,
  ColumnValidationDto,
  DbDataType,
  DefaultValueKind,
  GeneratedKind,
  IndexMethod,
  IndexType,
  ReferentialAction,
  TriggerEvent,
  TriggerScope,
  TriggerTiming,
} from '../schema/contract';

export interface EditorDefault {
  kind: DefaultValueKind;
  value?: string;
}

export interface EditorGenerated {
  kind: GeneratedKind;
  expression: string;
}

export interface EditorColumn {
  id: string;
  name: string;
  dataType: DbDataType;
  length?: number;
  precision?: number;
  scale?: number;
  fractionalSecondsPrecision?: number;
  isUnsigned: boolean;
  charset: Charset;
  collation: Collation;
  nullable: boolean;
  isPrimaryKey: boolean;
  isIdentity: boolean;
  default: EditorDefault;
  generated: EditorGenerated;
  validation: ColumnValidationDto;
  comment?: string;
}

export interface EditorIndex {
  id: string;
  name: string;
  type: IndexType;
  method: IndexMethod;
  columnIds: string[];
  filter?: string;
}

export interface EditorCheck {
  id: string;
  name: string;
  expression: string;
}

export interface EditorTable {
  id: string;
  name: string;
  schema?: string;
  x: number;
  y: number;
  columns: EditorColumn[];
  indexes: EditorIndex[];
  checks: EditorCheck[];
  comment?: string;
}

export interface EditorForeignKey {
  id: string;
  name: string;
  fromTable: string;
  toTable: string;
  columnPairs: { from: string; to: string }[];
  onDelete: ReferentialAction;
  onUpdate: ReferentialAction;
}

export interface EditorView {
  id: string;
  name: string;
  schema?: string;
  definition: string;
  isMaterialized: boolean;
  comment?: string;
}

export interface EditorSequence {
  id: string;
  name: string;
  schema?: string;
  dataType: DbDataType;
  startValue: number;
  increment: number;
  minValue?: number;
  maxValue?: number;
  cache?: number;
  cycle: boolean;
}

export interface EditorTrigger {
  id: string;
  name: string;
  schema?: string;
  tableName: string;
  timing: TriggerTiming;
  events: TriggerEvent[];
  scope: TriggerScope;
  whenCondition?: string;
  body: string;
}

export interface DesignerSnapshot {
  schemaName: string;
  tables: EditorTable[];
  foreignKeys: EditorForeignKey[];
  views: EditorView[];
  sequences: EditorSequence[];
  triggers: EditorTrigger[];
}

export interface DesignerState extends DesignerSnapshot {
  selectedTableId: string | null;

  setSchemaName: (name: string) => void;

  addTable: () => void;
  updateTable: (id: string, patch: Partial<EditorTable>) => void;
  removeTable: (id: string) => void;
  setTablePosition: (id: string, x: number, y: number) => void;
  selectTable: (id: string | null) => void;

  addColumn: (tableId: string) => void;
  updateColumn: (tableId: string, columnId: string, patch: Partial<EditorColumn>) => void;
  removeColumn: (tableId: string, columnId: string) => void;

  addIndex: (tableId: string) => void;
  updateIndex: (tableId: string, indexId: string, patch: Partial<EditorIndex>) => void;
  removeIndex: (tableId: string, indexId: string) => void;

  addCheck: (tableId: string) => void;
  updateCheck: (tableId: string, checkId: string, patch: Partial<EditorCheck>) => void;
  removeCheck: (tableId: string, checkId: string) => void;

  addForeignKey: (fk: { fromTable: string; fromColumn: string; toTable: string; toColumn: string }) => void;
  updateForeignKey: (id: string, patch: Partial<EditorForeignKey>) => void;
  removeForeignKey: (id: string) => void;

  addView: () => void;
  updateView: (id: string, patch: Partial<EditorView>) => void;
  removeView: (id: string) => void;

  addSequence: () => void;
  updateSequence: (id: string, patch: Partial<EditorSequence>) => void;
  removeSequence: (id: string) => void;

  addTrigger: () => void;
  updateTrigger: (id: string, patch: Partial<EditorTrigger>) => void;
  removeTrigger: (id: string) => void;

  replaceAll: (snapshot: DesignerSnapshot) => void;
}

const uid = (): string => crypto.randomUUID();

export const newColumn = (name = 'column', dataType: DbDataType = 'Integer'): EditorColumn => ({
  id: uid(),
  name,
  dataType,
  isUnsigned: false,
  charset: 'Default',
  collation: 'Default',
  nullable: false,
  isPrimaryKey: false,
  isIdentity: false,
  default: { kind: 'None' },
  generated: { kind: 'None', expression: '' },
  validation: { kind: 'None' },
});

const patchIn = <T extends { id: string }>(list: T[], id: string, patch: Partial<T>): T[] =>
  list.map((item) => (item.id === id ? { ...item, ...patch } : item));

export const useDesigner = create<DesignerState>((set, get) => ({
  schemaName: 'shop',
  tables: [],
  foreignKeys: [],
  views: [],
  sequences: [],
  triggers: [],
  selectedTableId: null,

  setSchemaName: (name) => set({ schemaName: name }),

  addTable: () => {
    const count = get().tables.length;
    const idColumn = newColumn('id', 'BigInt');
    idColumn.isPrimaryKey = true;
    idColumn.isIdentity = true;
    const table: EditorTable = {
      id: uid(),
      name: `table_${count + 1}`,
      schema: 'public',
      x: 80 + (count % 4) * 280,
      y: 80 + Math.floor(count / 4) * 240,
      columns: [idColumn],
      indexes: [],
      checks: [],
    };
    set((s) => ({ tables: [...s.tables, table], selectedTableId: table.id }));
  },

  updateTable: (id, patch) => set((s) => ({ tables: patchIn(s.tables, id, patch) })),

  removeTable: (id) =>
    set((s) => ({
      tables: s.tables.filter((t) => t.id !== id),
      foreignKeys: s.foreignKeys.filter((fk) => fk.fromTable !== id && fk.toTable !== id),
      selectedTableId: s.selectedTableId === id ? null : s.selectedTableId,
    })),

  setTablePosition: (id, x, y) => set((s) => ({ tables: patchIn(s.tables, id, { x, y }) })),

  selectTable: (id) => set({ selectedTableId: id }),

  addColumn: (tableId) =>
    set((s) => ({
      tables: patchIn(s.tables, tableId, {
        columns: [...(s.tables.find((t) => t.id === tableId)?.columns ?? []), newColumn()],
      }),
    })),

  updateColumn: (tableId, columnId, patch) =>
    set((s) => ({
      tables: s.tables.map((t) =>
        t.id === tableId ? { ...t, columns: patchIn(t.columns, columnId, patch) } : t,
      ),
    })),

  removeColumn: (tableId, columnId) =>
    set((s) => ({
      tables: s.tables.map((t) =>
        t.id === tableId
          ? {
              ...t,
              columns: t.columns.filter((c) => c.id !== columnId),
              indexes: t.indexes.map((i) => ({
                ...i,
                columnIds: i.columnIds.filter((cid) => cid !== columnId),
              })),
            }
          : t,
      ),
      foreignKeys: s.foreignKeys
        .map((fk) => ({
          ...fk,
          columnPairs: fk.columnPairs.filter((p) => p.from !== columnId && p.to !== columnId),
        }))
        .filter((fk) => fk.columnPairs.length > 0),
    })),

  addIndex: (tableId) =>
    set((s) => {
      const table = s.tables.find((t) => t.id === tableId);
      const index: EditorIndex = {
        id: uid(),
        name: `ix_${table?.name ?? 'table'}_${(table?.indexes.length ?? 0) + 1}`,
        type: 'NonUnique',
        method: 'BTree',
        columnIds: [],
      };
      return { tables: patchIn(s.tables, tableId, { indexes: [...(table?.indexes ?? []), index] }) };
    }),

  updateIndex: (tableId, indexId, patch) =>
    set((s) => ({
      tables: s.tables.map((t) =>
        t.id === tableId ? { ...t, indexes: patchIn(t.indexes, indexId, patch) } : t,
      ),
    })),

  removeIndex: (tableId, indexId) =>
    set((s) => ({
      tables: s.tables.map((t) =>
        t.id === tableId ? { ...t, indexes: t.indexes.filter((i) => i.id !== indexId) } : t,
      ),
    })),

  addCheck: (tableId) =>
    set((s) => {
      const table = s.tables.find((t) => t.id === tableId);
      const check: EditorCheck = {
        id: uid(),
        name: `ck_${table?.name ?? 'table'}_${(table?.checks.length ?? 0) + 1}`,
        expression: '',
      };
      return { tables: patchIn(s.tables, tableId, { checks: [...(table?.checks ?? []), check] }) };
    }),

  updateCheck: (tableId, checkId, patch) =>
    set((s) => ({
      tables: s.tables.map((t) =>
        t.id === tableId ? { ...t, checks: patchIn(t.checks, checkId, patch) } : t,
      ),
    })),

  removeCheck: (tableId, checkId) =>
    set((s) => ({
      tables: s.tables.map((t) =>
        t.id === tableId ? { ...t, checks: t.checks.filter((c) => c.id !== checkId) } : t,
      ),
    })),

  addForeignKey: ({ fromTable, fromColumn, toTable, toColumn }) => {
    const from = get().tables.find((t) => t.id === fromTable);
    const to = get().tables.find((t) => t.id === toTable);
    if (!from || !to) {
      return;
    }
    const fk: EditorForeignKey = {
      id: uid(),
      name: `fk_${from.name}_${to.name}`,
      fromTable,
      toTable,
      columnPairs: [{ from: fromColumn, to: toColumn }],
      onDelete: 'NoAction',
      onUpdate: 'NoAction',
    };
    set((s) => ({ foreignKeys: [...s.foreignKeys, fk] }));
  },

  updateForeignKey: (id, patch) =>
    set((s) => ({ foreignKeys: patchIn(s.foreignKeys, id, patch) })),

  removeForeignKey: (id) =>
    set((s) => ({ foreignKeys: s.foreignKeys.filter((fk) => fk.id !== id) })),

  addView: () =>
    set((s) => ({
      views: [
        ...s.views,
        {
          id: uid(),
          name: `view_${s.views.length + 1}`,
          schema: 'public',
          definition: 'SELECT 1',
          isMaterialized: false,
        },
      ],
    })),

  updateView: (id, patch) => set((s) => ({ views: patchIn(s.views, id, patch) })),

  removeView: (id) => set((s) => ({ views: s.views.filter((v) => v.id !== id) })),

  addSequence: () =>
    set((s) => ({
      sequences: [
        ...s.sequences,
        {
          id: uid(),
          name: `seq_${s.sequences.length + 1}`,
          schema: 'public',
          dataType: 'BigInt',
          startValue: 1,
          increment: 1,
          cycle: false,
        },
      ],
    })),

  updateSequence: (id, patch) => set((s) => ({ sequences: patchIn(s.sequences, id, patch) })),

  removeSequence: (id) => set((s) => ({ sequences: s.sequences.filter((q) => q.id !== id) })),

  addTrigger: () =>
    set((s) => ({
      triggers: [
        ...s.triggers,
        {
          id: uid(),
          name: `trg_${s.triggers.length + 1}`,
          schema: 'public',
          tableName: s.tables[0]?.name ?? '',
          timing: 'Before',
          events: ['Insert'],
          scope: 'Row',
          body: '',
        },
      ],
    })),

  updateTrigger: (id, patch) => set((s) => ({ triggers: patchIn(s.triggers, id, patch) })),

  removeTrigger: (id) => set((s) => ({ triggers: s.triggers.filter((t) => t.id !== id) })),

  replaceAll: (snapshot) => set({ ...snapshot, selectedTableId: null }),
}));
