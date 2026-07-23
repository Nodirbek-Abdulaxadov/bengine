import type { DbDataType, TriggerScope, TriggerTiming } from '../schema/contract';
import {
  SEQUENCE_DATA_TYPES,
  TRIGGER_EVENTS,
  TRIGGER_SCOPES,
  TRIGGER_TIMINGS,
} from '../schema/enums';
import { useDesigner } from '../state/store';

const num = (value: string): number | undefined => {
  const v = value.trim();
  return v === '' ? undefined : Number(v);
};

/** Editors for the schema-level objects: views, sequences, and triggers. */
export function SchemaObjects() {
  const store = useDesigner();
  const { views, sequences, triggers, tables } = store;

  return (
    <div className="objects">
      <div className="section">
        <span>Views</span>
        <button onClick={store.addView}>+ view</button>
      </div>
      {views.map((view) => (
        <div key={view.id} className="col">
          <div className="row">
            <input className="grow" value={view.name} onChange={(e) => store.updateView(view.id, { name: e.target.value })} />
            <button className="danger" onClick={() => store.removeView(view.id)}>
              ✕
            </button>
          </div>
          <div className="row">
            <input
              className="grow"
              placeholder="schema"
              value={view.schema ?? ''}
              onChange={(e) => store.updateView(view.id, { schema: e.target.value || undefined })}
            />
            <label>
              <input
                type="checkbox"
                checked={view.isMaterialized}
                onChange={(e) => store.updateView(view.id, { isMaterialized: e.target.checked })}
              />
              materialized
            </label>
          </div>
          <textarea
            rows={3}
            value={view.definition}
            onChange={(e) => store.updateView(view.id, { definition: e.target.value })}
          />
        </div>
      ))}

      <div className="section">
        <span>Sequences</span>
        <button onClick={store.addSequence}>+ sequence</button>
      </div>
      {sequences.map((seq) => (
        <div key={seq.id} className="col">
          <div className="row">
            <input className="grow" value={seq.name} onChange={(e) => store.updateSequence(seq.id, { name: e.target.value })} />
            <button className="danger" onClick={() => store.removeSequence(seq.id)}>
              ✕
            </button>
          </div>
          <div className="row">
            <select value={seq.dataType} onChange={(e) => store.updateSequence(seq.id, { dataType: e.target.value as DbDataType })}>
              {SEQUENCE_DATA_TYPES.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
            <label>
              <input type="checkbox" checked={seq.cycle} onChange={(e) => store.updateSequence(seq.id, { cycle: e.target.checked })} />
              cycle
            </label>
          </div>
          <div className="row">
            <label>start/inc</label>
            <input type="number" value={seq.startValue} onChange={(e) => store.updateSequence(seq.id, { startValue: Number(e.target.value) })} />
            <input type="number" value={seq.increment} onChange={(e) => store.updateSequence(seq.id, { increment: Number(e.target.value) })} />
          </div>
          <div className="row">
            <label>min/max</label>
            <input type="number" value={seq.minValue ?? ''} onChange={(e) => store.updateSequence(seq.id, { minValue: num(e.target.value) })} />
            <input type="number" value={seq.maxValue ?? ''} onChange={(e) => store.updateSequence(seq.id, { maxValue: num(e.target.value) })} />
          </div>
        </div>
      ))}

      <div className="section">
        <span>Triggers</span>
        <button onClick={store.addTrigger}>+ trigger</button>
      </div>
      {triggers.map((trigger) => (
        <div key={trigger.id} className="col">
          <div className="row">
            <input className="grow" value={trigger.name} onChange={(e) => store.updateTrigger(trigger.id, { name: e.target.value })} />
            <button className="danger" onClick={() => store.removeTrigger(trigger.id)}>
              ✕
            </button>
          </div>
          <div className="row">
            <label>table</label>
            <select value={trigger.tableName} onChange={(e) => store.updateTrigger(trigger.id, { tableName: e.target.value })}>
              <option value="">—</option>
              {tables.map((t) => (
                <option key={t.id} value={t.name}>
                  {t.name}
                </option>
              ))}
            </select>
          </div>
          <div className="row">
            <select value={trigger.timing} onChange={(e) => store.updateTrigger(trigger.id, { timing: e.target.value as TriggerTiming })}>
              {TRIGGER_TIMINGS.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
            <select value={trigger.scope} onChange={(e) => store.updateTrigger(trigger.id, { scope: e.target.value as TriggerScope })}>
              {TRIGGER_SCOPES.map((s) => (
                <option key={s} value={s}>
                  {s}
                </option>
              ))}
            </select>
          </div>
          <div className="flags">
            {TRIGGER_EVENTS.map((ev) => (
              <label key={ev}>
                <input
                  type="checkbox"
                  checked={trigger.events.includes(ev)}
                  onChange={(e) =>
                    store.updateTrigger(trigger.id, {
                      events: e.target.checked
                        ? [...trigger.events, ev]
                        : trigger.events.filter((x) => x !== ev),
                    })
                  }
                />
                {ev}
              </label>
            ))}
          </div>
          <textarea
            rows={3}
            placeholder="trigger body"
            value={trigger.body}
            onChange={(e) => store.updateTrigger(trigger.id, { body: e.target.value })}
          />
        </div>
      ))}
    </div>
  );
}
