import type { ChangeEvent } from 'react';
import type {
  ColumnValidationDto,
  DbDataType,
  DefaultValueKind,
  GeneratedKind,
} from '../schema/contract';
import {
  CHARSETS,
  COLLATIONS,
  DB_DATA_TYPES,
  DEFAULT_VALUE_KINDS,
  GENERATED_KINDS,
  supportsCollation,
  supportsFractionalSeconds,
  supportsIdentity,
  supportsLength,
  supportsPrecision,
  supportsUnsigned,
  VALIDATION_KINDS,
  validationKindFor,
} from '../schema/enums';
import { useDesigner, type EditorColumn } from '../state/store';

const num = (e: ChangeEvent<HTMLInputElement>): number | undefined => {
  const v = e.target.value.trim();
  return v === '' ? undefined : Number(v);
};

const hasValue = (kind: DefaultValueKind): boolean =>
  kind === 'Literal' || kind === 'Expression' || kind === 'Function';

export function ColumnEditor({ tableId, column }: { tableId: string; column: EditorColumn }) {
  const { updateColumn, removeColumn } = useDesigner();
  const set = (patch: Partial<EditorColumn>) => updateColumn(tableId, column.id, patch);

  const setValidation = (patch: Partial<ColumnValidationDto>) =>
    set({ validation: { ...column.validation, ...patch } });

  return (
    <div className="col">
      <div className="row">
        <input className="grow" value={column.name} onChange={(e) => set({ name: e.target.value })} />
        <button className="danger" onClick={() => removeColumn(tableId, column.id)}>
          ✕
        </button>
      </div>

      <div className="row">
        <select
          className="grow"
          value={column.dataType}
          onChange={(e) => {
            const dataType = e.target.value as DbDataType;
            set({ dataType, validation: { kind: validationKindFor(dataType) } });
          }}
        >
          {DB_DATA_TYPES.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </div>

      {supportsLength(column.dataType) && (
        <div className="row">
          <label>length</label>
          <input type="number" value={column.length ?? ''} onChange={(e) => set({ length: num(e) })} />
        </div>
      )}

      {supportsPrecision(column.dataType) && (
        <div className="row">
          <label>prec/scale</label>
          <input
            type="number"
            value={column.precision ?? ''}
            onChange={(e) => set({ precision: num(e) })}
          />
          <input type="number" value={column.scale ?? ''} onChange={(e) => set({ scale: num(e) })} />
        </div>
      )}

      {supportsFractionalSeconds(column.dataType) && (
        <div className="row">
          <label>frac sec</label>
          <input
            type="number"
            value={column.fractionalSecondsPrecision ?? ''}
            onChange={(e) => set({ fractionalSecondsPrecision: num(e) })}
          />
        </div>
      )}

      <div className="flags">
        <label>
          <input type="checkbox" checked={column.nullable} onChange={(e) => set({ nullable: e.target.checked })} />
          null
        </label>
        <label>
          <input
            type="checkbox"
            checked={column.isPrimaryKey}
            onChange={(e) => set({ isPrimaryKey: e.target.checked })}
          />
          PK
        </label>
        {supportsIdentity(column.dataType) && (
          <label>
            <input
              type="checkbox"
              checked={column.isIdentity}
              onChange={(e) => set({ isIdentity: e.target.checked })}
            />
            identity
          </label>
        )}
        {supportsUnsigned(column.dataType) && (
          <label>
            <input
              type="checkbox"
              checked={column.isUnsigned}
              onChange={(e) => set({ isUnsigned: e.target.checked })}
            />
            unsigned
          </label>
        )}
      </div>

      <details className="advanced">
        <summary>batafsil</summary>

        {supportsCollation(column.dataType) && (
          <>
            <div className="row">
              <label>charset</label>
              <select value={column.charset} onChange={(e) => set({ charset: e.target.value as EditorColumn['charset'] })}>
                {CHARSETS.map((c) => (
                  <option key={c} value={c}>
                    {c}
                  </option>
                ))}
              </select>
            </div>
            <div className="row">
              <label>collation</label>
              <select
                value={column.collation}
                onChange={(e) => set({ collation: e.target.value as EditorColumn['collation'] })}
              >
                {COLLATIONS.map((c) => (
                  <option key={c} value={c}>
                    {c}
                  </option>
                ))}
              </select>
            </div>
          </>
        )}

        <div className="row">
          <label>default</label>
          <select
            value={column.default.kind}
            onChange={(e) =>
              set({ default: { kind: e.target.value as DefaultValueKind, value: column.default.value } })
            }
          >
            {DEFAULT_VALUE_KINDS.map((k) => (
              <option key={k} value={k}>
                {k}
              </option>
            ))}
          </select>
        </div>
        {hasValue(column.default.kind) && (
          <div className="row">
            <input
              className="grow"
              placeholder="default qiymat / ifoda"
              value={column.default.value ?? ''}
              onChange={(e) => set({ default: { kind: column.default.kind, value: e.target.value } })}
            />
          </div>
        )}

        <div className="row">
          <label>generated</label>
          <select
            value={column.generated.kind}
            onChange={(e) =>
              set({ generated: { kind: e.target.value as GeneratedKind, expression: column.generated.expression } })
            }
          >
            {GENERATED_KINDS.map((k) => (
              <option key={k} value={k}>
                {k}
              </option>
            ))}
          </select>
        </div>
        {column.generated.kind !== 'None' && (
          <div className="row">
            <input
              className="grow"
              placeholder="ifoda, masalan price * qty"
              value={column.generated.expression}
              onChange={(e) => set({ generated: { kind: column.generated.kind, expression: e.target.value } })}
            />
          </div>
        )}

        <div className="row">
          <label>validation</label>
          <select value={column.validation.kind} onChange={(e) => setValidation({ kind: e.target.value as ColumnValidationDto['kind'] })}>
            {VALIDATION_KINDS.map((k) => (
              <option key={k} value={k}>
                {k}
              </option>
            ))}
          </select>
        </div>

        {column.validation.kind === 'Numeric' && (
          <div className="row">
            <input
              type="number"
              placeholder="min"
              value={column.validation.numeric?.minValue ?? ''}
              onChange={(e) => setValidation({ numeric: { ...column.validation.numeric, minValue: num(e) } })}
            />
            <input
              type="number"
              placeholder="max"
              value={column.validation.numeric?.maxValue ?? ''}
              onChange={(e) => setValidation({ numeric: { ...column.validation.numeric, maxValue: num(e) } })}
            />
          </div>
        )}

        {column.validation.kind === 'String' && (
          <div className="row">
            <input
              type="number"
              placeholder="minLen"
              value={column.validation.string?.minLength ?? ''}
              onChange={(e) => setValidation({ string: { ...column.validation.string, minLength: num(e) } })}
            />
            <input
              type="number"
              placeholder="maxLen"
              value={column.validation.string?.maxLength ?? ''}
              onChange={(e) => setValidation({ string: { ...column.validation.string, maxLength: num(e) } })}
            />
          </div>
        )}

        {column.validation.kind === 'Date' && (
          <div className="flags">
            <label>
              <input
                type="checkbox"
                checked={column.validation.date?.allowFuture ?? true}
                onChange={(e) => setValidation({ date: { ...column.validation.date, allowFuture: e.target.checked } })}
              />
              future
            </label>
            <label>
              <input
                type="checkbox"
                checked={column.validation.date?.allowPast ?? true}
                onChange={(e) => setValidation({ date: { ...column.validation.date, allowPast: e.target.checked } })}
              />
              past
            </label>
          </div>
        )}

        <div className="row">
          <label>comment</label>
          <input
            className="grow"
            value={column.comment ?? ''}
            onChange={(e) => set({ comment: e.target.value || undefined })}
          />
        </div>
      </details>
    </div>
  );
}
