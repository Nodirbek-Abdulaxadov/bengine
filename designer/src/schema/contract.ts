// TypeScript mirror of the Bengine C# schema.json contract.
// Enum values are the exact C# enum member names (serialized by name), so a
// document produced here deserializes directly into Bengine's DatabaseSchema.

export type DbDataType =
  | 'Boolean'
  | 'TinyInt'
  | 'SmallInt'
  | 'Integer'
  | 'BigInt'
  | 'Decimal'
  | 'Real'
  | 'Double'
  | 'Char'
  | 'VarChar'
  | 'Text'
  | 'Binary'
  | 'VarBinary'
  | 'Blob'
  | 'Date'
  | 'Time'
  | 'Timestamp'
  | 'TimestampTz'
  | 'Uuid'
  | 'Json'
  | 'JsonBinary'
  | 'Xml';

export type Nullability = 'NotNull' | 'Nullable';

export type IdentityStrategy = 'None' | 'Identity' | 'Sequence' | 'AutoIncrement';

export type ReferentialAction =
  | 'NoAction'
  | 'Restrict'
  | 'Cascade'
  | 'SetNull'
  | 'SetDefault';

export type IndexType = 'NonUnique' | 'Unique' | 'FullText' | 'Spatial';

export type IndexMethod = 'BTree' | 'Hash' | 'Gin' | 'Gist' | 'Brin' | 'SpGist';

export type SortDirection = 'Ascending' | 'Descending';

export type NullsOrder = 'Default' | 'First' | 'Last';

export type Charset = 'Default' | 'Utf8' | 'Utf8Mb4' | 'Utf16' | 'Latin1' | 'Ascii';

export type Collation =
  | 'Default'
  | 'Binary'
  | 'CaseSensitive'
  | 'CaseInsensitive'
  | 'AccentInsensitive';

export type StorageStrategy = 'Default' | 'Plain' | 'Main' | 'External' | 'Extended';

export type GeneratedKind = 'None' | 'Stored' | 'Virtual';

export type ValidationKind = 'None' | 'Numeric' | 'String' | 'Date' | 'Binary';

export type DefaultValueKind =
  | 'None'
  | 'Literal'
  | 'Expression'
  | 'Function'
  | 'CurrentTimestamp'
  | 'CurrentDate'
  | 'CurrentTime'
  | 'Null';

export type TriggerTiming = 'Before' | 'After' | 'InsteadOf';

/** Single flags value; combine several as a comma-separated string, e.g. "Insert, Update". */
export type TriggerEvent = 'Insert' | 'Update' | 'Delete' | 'Truncate';

export type TriggerScope = 'Row' | 'Statement';

export interface ColumnTypeDto {
  dataType: DbDataType;
  length?: number;
  precision?: number;
  scale?: number;
  fractionalSecondsPrecision?: number;
  isUnsigned?: boolean;
  charset?: Charset;
  collation?: Collation;
  storage?: StorageStrategy;
}

export interface DefaultValueDto {
  kind: DefaultValueKind;
  value?: string;
}

export interface IdentityDto {
  strategy: IdentityStrategy;
  sequenceName?: string;
  startValue?: number;
  increment?: number;
}

export interface GeneratedColumnDto {
  kind: GeneratedKind;
  expression: string;
}

export interface NumericValidationDto {
  minValue?: number;
  maxValue?: number;
  precision?: number;
  scale?: number;
  allowNegative?: boolean;
}

export interface StringValidationDto {
  minLength?: number;
  maxLength?: number;
  pattern?: string;
  allowEmpty?: boolean;
}

export interface DateValidationDto {
  minValue?: string;
  maxValue?: string;
  allowFuture?: boolean;
  allowPast?: boolean;
}

export interface ColumnValidationDto {
  kind: ValidationKind;
  numeric?: NumericValidationDto;
  string?: StringValidationDto;
  date?: DateValidationDto;
}

export interface ColumnDto {
  name: string;
  type: ColumnTypeDto;
  nullability?: Nullability;
  default?: DefaultValueDto;
  identity?: IdentityDto;
  generated?: GeneratedColumnDto;
  validation?: ColumnValidationDto;
  comment?: string;
}

export interface PrimaryKeyDto {
  name?: string;
  columnNames: string[];
}

export interface ForeignKeyDto {
  name: string;
  columns: string[];
  referencedTable: string;
  referencedSchema?: string;
  referencedColumns: string[];
  onDelete?: ReferentialAction;
  onUpdate?: ReferentialAction;
  isDeferrable?: boolean;
}

export interface IndexColumnDto {
  columnName: string;
  direction?: SortDirection;
  nulls?: NullsOrder;
}

export interface IndexDto {
  name: string;
  type?: IndexType;
  method?: IndexMethod;
  columns: IndexColumnDto[];
  filter?: string;
  comment?: string;
}

export interface CheckConstraintDto {
  name: string;
  expression: string;
}

export interface TableDto {
  name: string;
  schema?: string;
  columns: ColumnDto[];
  primaryKey?: PrimaryKeyDto;
  foreignKeys?: ForeignKeyDto[];
  indexes?: IndexDto[];
  checkConstraints?: CheckConstraintDto[];
  comment?: string;
}

export interface ViewDto {
  name: string;
  schema?: string;
  definition: string;
  columns?: string[];
  isMaterialized?: boolean;
  comment?: string;
}

export interface SequenceDto {
  name: string;
  schema?: string;
  dataType?: DbDataType;
  startValue?: number;
  increment?: number;
  minValue?: number;
  maxValue?: number;
  cache?: number;
  cycle?: boolean;
}

export interface TriggerDto {
  name: string;
  schema?: string;
  tableName: string;
  timing: TriggerTiming;
  events: string;
  scope?: TriggerScope;
  whenCondition?: string;
  body: string;
}

export interface SchemaDocument {
  name: string;
  tables: TableDto[];
  views?: ViewDto[];
  sequences?: SequenceDto[];
  triggers?: TriggerDto[];
  comment?: string;
}
