import type {
  Charset,
  Collation,
  DbDataType,
  DefaultValueKind,
  GeneratedKind,
  IndexMethod,
  IndexType,
  NullsOrder,
  ReferentialAction,
  SortDirection,
  StorageStrategy,
  TriggerEvent,
  TriggerScope,
  TriggerTiming,
  ValidationKind,
} from './contract';

/** All logical data types, in the order shown in the type dropdown. */
export const DB_DATA_TYPES: DbDataType[] = [
  'Boolean',
  'TinyInt',
  'SmallInt',
  'Integer',
  'BigInt',
  'Decimal',
  'Real',
  'Double',
  'Char',
  'VarChar',
  'Text',
  'Binary',
  'VarBinary',
  'Blob',
  'Date',
  'Time',
  'Timestamp',
  'TimestampTz',
  'Uuid',
  'Json',
  'JsonBinary',
  'Xml',
];

export const REFERENTIAL_ACTIONS: ReferentialAction[] = [
  'NoAction',
  'Restrict',
  'Cascade',
  'SetNull',
  'SetDefault',
];

export const INDEX_TYPES: IndexType[] = ['NonUnique', 'Unique', 'FullText', 'Spatial'];

export const INDEX_METHODS: IndexMethod[] = ['BTree', 'Hash', 'Gin', 'Gist', 'Brin', 'SpGist'];

export const SORT_DIRECTIONS: SortDirection[] = ['Ascending', 'Descending'];

export const NULLS_ORDERS: NullsOrder[] = ['Default', 'First', 'Last'];

export const CHARSETS: Charset[] = ['Default', 'Utf8', 'Utf8Mb4', 'Utf16', 'Latin1', 'Ascii'];

export const COLLATIONS: Collation[] = [
  'Default',
  'Binary',
  'CaseSensitive',
  'CaseInsensitive',
  'AccentInsensitive',
];

export const STORAGE_STRATEGIES: StorageStrategy[] = [
  'Default',
  'Plain',
  'Main',
  'External',
  'Extended',
];

export const GENERATED_KINDS: GeneratedKind[] = ['None', 'Stored', 'Virtual'];

export const VALIDATION_KINDS: ValidationKind[] = ['None', 'Numeric', 'String', 'Date', 'Binary'];

export const DEFAULT_VALUE_KINDS: DefaultValueKind[] = [
  'None',
  'Literal',
  'Expression',
  'Function',
  'CurrentTimestamp',
  'CurrentDate',
  'CurrentTime',
  'Null',
];

export const TRIGGER_TIMINGS: TriggerTiming[] = ['Before', 'After', 'InsteadOf'];

export const TRIGGER_EVENTS: TriggerEvent[] = ['Insert', 'Update', 'Delete', 'Truncate'];

export const TRIGGER_SCOPES: TriggerScope[] = ['Row', 'Statement'];

export const SEQUENCE_DATA_TYPES: DbDataType[] = ['SmallInt', 'Integer', 'BigInt'];

// --- Capability helpers (mirror of Bengine metadata, for enabling UI inputs) ---

const LENGTH_TYPES = new Set<DbDataType>(['Char', 'VarChar', 'Binary', 'VarBinary']);
const DECIMAL_TYPES = new Set<DbDataType>(['Decimal']);
const INTEGER_TYPES = new Set<DbDataType>(['TinyInt', 'SmallInt', 'Integer', 'BigInt']);
const TEXT_TYPES = new Set<DbDataType>(['Char', 'VarChar', 'Text']);
const TEMPORAL_TYPES = new Set<DbDataType>(['Time', 'Timestamp', 'TimestampTz']);

export const supportsLength = (type: DbDataType): boolean => LENGTH_TYPES.has(type);
export const supportsPrecision = (type: DbDataType): boolean => DECIMAL_TYPES.has(type);
export const supportsIdentity = (type: DbDataType): boolean => INTEGER_TYPES.has(type);
export const supportsUnsigned = (type: DbDataType): boolean => INTEGER_TYPES.has(type);
export const supportsCollation = (type: DbDataType): boolean => TEXT_TYPES.has(type);
export const supportsFractionalSeconds = (type: DbDataType): boolean => TEMPORAL_TYPES.has(type);

/** The default validation family that fits a logical type. */
export function validationKindFor(type: DbDataType): ValidationKind {
  if (INTEGER_TYPES.has(type) || DECIMAL_TYPES.has(type) || type === 'Real' || type === 'Double') {
    return 'Numeric';
  }
  if (TEXT_TYPES.has(type)) {
    return 'String';
  }
  if (type === 'Date' || TEMPORAL_TYPES.has(type)) {
    return 'Date';
  }
  if (type === 'Binary' || type === 'VarBinary' || type === 'Blob') {
    return 'Binary';
  }
  return 'None';
}
