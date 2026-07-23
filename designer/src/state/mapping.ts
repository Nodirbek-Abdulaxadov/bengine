import type {
  ColumnDto,
  ColumnTypeDto,
  ForeignKeyDto,
  IndexDto,
  PrimaryKeyDto,
  SchemaDocument,
  SequenceDto,
  TableDto,
  TriggerDto,
  TriggerEvent,
  ViewDto,
} from '../schema/contract';
import {
  supportsCollation,
  supportsFractionalSeconds,
  supportsLength,
  supportsPrecision,
  supportsUnsigned,
  TRIGGER_EVENTS,
} from '../schema/enums';
import {
  newColumn,
  type DesignerSnapshot,
  type EditorColumn,
  type EditorForeignKey,
  type EditorIndex,
  type EditorSequence,
  type EditorTable,
  type EditorTrigger,
  type EditorView,
} from './store';

// ---------------------------------------------------------------------------
// Editor state  ->  schema.json
// ---------------------------------------------------------------------------

export function toSchemaDocument(state: DesignerSnapshot): SchemaDocument {
  const columnName = new Map<string, string>();
  const tableName = new Map<string, string>();
  const tableSchema = new Map<string, string | undefined>();

  for (const table of state.tables) {
    tableName.set(table.id, table.name);
    tableSchema.set(table.id, table.schema);
    for (const column of table.columns) {
      columnName.set(column.id, column.name);
    }
  }

  const resolveCol = (id: string) => columnName.get(id) ?? id;

  const tables: TableDto[] = state.tables.map((table) => {
    const dto: TableDto = {
      name: table.name,
      columns: table.columns.map(toColumnDto),
    };
    if (table.schema) {
      dto.schema = table.schema;
    }

    const primaryKey = toPrimaryKey(table);
    if (primaryKey) {
      dto.primaryKey = primaryKey;
    }

    const foreignKeys = state.foreignKeys
      .filter((fk) => fk.fromTable === table.id)
      .map((fk) => toForeignKeyDto(fk, resolveCol, tableName, tableSchema));
    if (foreignKeys.length > 0) {
      dto.foreignKeys = foreignKeys;
    }

    const indexes = table.indexes
      .filter((index) => index.columnIds.length > 0)
      .map((index) => toIndexDto(index, resolveCol));
    if (indexes.length > 0) {
      dto.indexes = indexes;
    }

    const checks = table.checks
      .filter((c) => c.expression.trim() !== '')
      .map((c) => ({ name: c.name, expression: c.expression }));
    if (checks.length > 0) {
      dto.checkConstraints = checks;
    }

    if (table.comment) {
      dto.comment = table.comment;
    }
    return dto;
  });

  const doc: SchemaDocument = { name: state.schemaName, tables };
  if (state.views.length > 0) {
    doc.views = state.views.map(toViewDto);
  }
  if (state.sequences.length > 0) {
    doc.sequences = state.sequences.map(toSequenceDto);
  }
  if (state.triggers.length > 0) {
    doc.triggers = state.triggers.map(toTriggerDto);
  }
  return doc;
}

function toColumnDto(column: EditorColumn): ColumnDto {
  const type: ColumnTypeDto = { dataType: column.dataType };
  if (supportsLength(column.dataType) && column.length != null) {
    type.length = column.length;
  }
  if (supportsPrecision(column.dataType)) {
    if (column.precision != null) {
      type.precision = column.precision;
    }
    if (column.scale != null) {
      type.scale = column.scale;
    }
  }
  if (supportsFractionalSeconds(column.dataType) && column.fractionalSecondsPrecision != null) {
    type.fractionalSecondsPrecision = column.fractionalSecondsPrecision;
  }
  if (supportsUnsigned(column.dataType) && column.isUnsigned) {
    type.isUnsigned = true;
  }
  if (supportsCollation(column.dataType)) {
    if (column.charset !== 'Default') {
      type.charset = column.charset;
    }
    if (column.collation !== 'Default') {
      type.collation = column.collation;
    }
  }

  const dto: ColumnDto = {
    name: column.name,
    type,
    nullability: column.nullable ? 'Nullable' : 'NotNull',
  };
  if (column.isIdentity) {
    dto.identity = { strategy: 'Identity' };
  }
  if (column.default.kind !== 'None') {
    dto.default = column.default.value
      ? { kind: column.default.kind, value: column.default.value }
      : { kind: column.default.kind };
  }
  if (column.generated.kind !== 'None' && column.generated.expression.trim() !== '') {
    dto.generated = { kind: column.generated.kind, expression: column.generated.expression };
  }
  if (column.validation.kind !== 'None') {
    dto.validation = column.validation;
  }
  if (column.comment) {
    dto.comment = column.comment;
  }
  return dto;
}

function toPrimaryKey(table: EditorTable): PrimaryKeyDto | undefined {
  const columnNames = table.columns.filter((c) => c.isPrimaryKey).map((c) => c.name);
  return columnNames.length > 0 ? { name: `pk_${table.name}`, columnNames } : undefined;
}

function toForeignKeyDto(
  fk: EditorForeignKey,
  resolveCol: (id: string) => string,
  tableName: Map<string, string>,
  tableSchema: Map<string, string | undefined>,
): ForeignKeyDto {
  const dto: ForeignKeyDto = {
    name: fk.name,
    columns: fk.columnPairs.map((p) => resolveCol(p.from)),
    referencedTable: tableName.get(fk.toTable) ?? fk.toTable,
    referencedColumns: fk.columnPairs.map((p) => resolveCol(p.to)),
    onDelete: fk.onDelete,
    onUpdate: fk.onUpdate,
  };
  const schema = tableSchema.get(fk.toTable);
  if (schema) {
    dto.referencedSchema = schema;
  }
  return dto;
}

function toIndexDto(index: EditorIndex, resolveCol: (id: string) => string): IndexDto {
  const dto: IndexDto = {
    name: index.name,
    type: index.type,
    method: index.method,
    columns: index.columnIds.map((id) => ({ columnName: resolveCol(id) })),
  };
  if (index.filter) {
    dto.filter = index.filter;
  }
  return dto;
}

function toViewDto(view: EditorView): ViewDto {
  const dto: ViewDto = { name: view.name, definition: view.definition };
  if (view.schema) {
    dto.schema = view.schema;
  }
  if (view.isMaterialized) {
    dto.isMaterialized = true;
  }
  if (view.comment) {
    dto.comment = view.comment;
  }
  return dto;
}

function toSequenceDto(sequence: EditorSequence): SequenceDto {
  const dto: SequenceDto = {
    name: sequence.name,
    dataType: sequence.dataType,
    startValue: sequence.startValue,
    increment: sequence.increment,
    cycle: sequence.cycle,
  };
  if (sequence.schema) {
    dto.schema = sequence.schema;
  }
  if (sequence.minValue != null) {
    dto.minValue = sequence.minValue;
  }
  if (sequence.maxValue != null) {
    dto.maxValue = sequence.maxValue;
  }
  if (sequence.cache != null) {
    dto.cache = sequence.cache;
  }
  return dto;
}

function toTriggerDto(trigger: EditorTrigger): TriggerDto {
  const dto: TriggerDto = {
    name: trigger.name,
    tableName: trigger.tableName,
    timing: trigger.timing,
    events: trigger.events.join(', '),
    scope: trigger.scope,
    body: trigger.body,
  };
  if (trigger.schema) {
    dto.schema = trigger.schema;
  }
  if (trigger.whenCondition) {
    dto.whenCondition = trigger.whenCondition;
  }
  return dto;
}

// ---------------------------------------------------------------------------
// schema.json  ->  editor state
// ---------------------------------------------------------------------------

export function fromSchemaDocument(doc: SchemaDocument): DesignerSnapshot {
  const uid = (): string => crypto.randomUUID();
  const tableIdByName = new Map<string, string>();
  const columnIdByKey = new Map<string, string>();

  const tables: EditorTable[] = doc.tables.map((table, index) => {
    const tableId = uid();
    tableIdByName.set(table.name, tableId);
    const pkNames = new Set(table.primaryKey?.columnNames ?? []);

    const columns: EditorColumn[] = table.columns.map((column) => {
      const base = newColumn(column.name, column.type.dataType);
      const columnId = base.id;
      columnIdByKey.set(`${table.name}.${column.name}`, columnId);
      return {
        ...base,
        length: column.type.length,
        precision: column.type.precision,
        scale: column.type.scale,
        fractionalSecondsPrecision: column.type.fractionalSecondsPrecision,
        isUnsigned: column.type.isUnsigned ?? false,
        charset: column.type.charset ?? 'Default',
        collation: column.type.collation ?? 'Default',
        nullable: column.nullability === 'Nullable',
        isPrimaryKey: pkNames.has(column.name),
        isIdentity: column.identity != null && column.identity.strategy !== 'None',
        default: column.default ?? { kind: 'None' },
        generated: column.generated ?? { kind: 'None', expression: '' },
        validation: column.validation ?? { kind: 'None' },
        comment: column.comment,
      };
    });

    const columnIdInTable = (name: string) => columnIdByKey.get(`${table.name}.${name}`);

    const indexes: EditorIndex[] = (table.indexes ?? []).map((idx) => ({
      id: uid(),
      name: idx.name,
      type: idx.type ?? 'NonUnique',
      method: idx.method ?? 'BTree',
      columnIds: idx.columns
        .map((c) => columnIdInTable(c.columnName))
        .filter((id): id is string => id != null),
      filter: idx.filter,
    }));

    const checks = (table.checkConstraints ?? []).map((c) => ({
      id: uid(),
      name: c.name,
      expression: c.expression,
    }));

    return {
      id: tableId,
      name: table.name,
      schema: table.schema,
      x: 80 + (index % 4) * 280,
      y: 80 + Math.floor(index / 4) * 240,
      columns,
      indexes,
      checks,
      comment: table.comment,
    };
  });

  const foreignKeys: EditorForeignKey[] = [];
  for (const table of doc.tables) {
    for (const fk of table.foreignKeys ?? []) {
      const fromTable = tableIdByName.get(table.name);
      const toTable = tableIdByName.get(fk.referencedTable);
      if (!fromTable || !toTable) {
        continue;
      }
      const columnPairs = fk.columns
        .map((col, i) => {
          const from = columnIdByKey.get(`${table.name}.${col}`);
          const to = columnIdByKey.get(`${fk.referencedTable}.${fk.referencedColumns[i]}`);
          return from && to ? { from, to } : null;
        })
        .filter((p): p is { from: string; to: string } => p != null);
      if (columnPairs.length > 0) {
        foreignKeys.push({
          id: uid(),
          name: fk.name,
          fromTable,
          toTable,
          columnPairs,
          onDelete: fk.onDelete ?? 'NoAction',
          onUpdate: fk.onUpdate ?? 'NoAction',
        });
      }
    }
  }

  const views: EditorView[] = (doc.views ?? []).map((v) => ({
    id: uid(),
    name: v.name,
    schema: v.schema,
    definition: v.definition,
    isMaterialized: v.isMaterialized ?? false,
    comment: v.comment,
  }));

  const sequences: EditorSequence[] = (doc.sequences ?? []).map((q) => ({
    id: uid(),
    name: q.name,
    schema: q.schema,
    dataType: q.dataType ?? 'BigInt',
    startValue: q.startValue ?? 1,
    increment: q.increment ?? 1,
    minValue: q.minValue,
    maxValue: q.maxValue,
    cache: q.cache,
    cycle: q.cycle ?? false,
  }));

  const parseEvents = (value: string): TriggerEvent[] => {
    const valid = new Set<string>(TRIGGER_EVENTS);
    return value
      .split(',')
      .map((s) => s.trim())
      .filter((s) => valid.has(s)) as TriggerEvent[];
  };

  const triggers: EditorTrigger[] = (doc.triggers ?? []).map((t) => ({
    id: uid(),
    name: t.name,
    schema: t.schema,
    tableName: t.tableName,
    timing: t.timing,
    events: parseEvents(t.events),
    scope: t.scope ?? 'Row',
    whenCondition: t.whenCondition,
    body: t.body,
  }));

  return { schemaName: doc.name, tables, foreignKeys, views, sequences, triggers };
}
