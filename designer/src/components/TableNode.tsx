import { Handle, Position, type Node, type NodeProps } from '@xyflow/react';
import type { EditorTable } from '../state/store';

export type TableNodeData = { table: EditorTable };
export type TableNodeType = Node<TableNodeData, 'table'>;

/** Renders a table as a titled card with one connectable handle per column. */
export function TableNode({ data, selected }: NodeProps<TableNodeType>) {
  const { table } = data;

  return (
    <div className={`table-node${selected ? ' selected' : ''}`}>
      <div className="table-node__title">
        {table.schema ? `${table.schema}.` : ''}
        {table.name}
      </div>
      <div className="table-node__columns">
        {table.columns.map((column) => (
          <div key={column.id} className="table-node__column">
            <Handle
              type="target"
              position={Position.Left}
              id={column.id}
              className="table-node__handle"
            />
            <span className="table-node__column-name">
              {column.isPrimaryKey ? '🔑 ' : ''}
              {column.name}
            </span>
            <span className="table-node__column-type">
              {column.dataType}
              {column.nullable ? '?' : ''}
            </span>
            <Handle
              type="source"
              position={Position.Right}
              id={column.id}
              className="table-node__handle"
            />
          </div>
        ))}
      </div>
    </div>
  );
}
