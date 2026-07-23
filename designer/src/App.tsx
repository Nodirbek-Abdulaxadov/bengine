import { useCallback, useMemo } from 'react';
import {
  Background,
  Controls,
  MarkerType,
  ReactFlow,
  ReactFlowProvider,
  type Connection,
  type Edge,
  type Node,
  type NodeChange,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { TableNode, type TableNodeType } from './components/TableNode';
import { RightPanel } from './components/RightPanel';
import { Toolbar } from './components/Toolbar';
import { useDesigner } from './state/store';

const nodeTypes = { table: TableNode };

function Canvas() {
  const tables = useDesigner((s) => s.tables);
  const foreignKeys = useDesigner((s) => s.foreignKeys);
  const { setTablePosition, selectTable, addForeignKey } = useDesigner();

  const nodes = useMemo<Node[]>(
    () =>
      tables.map<TableNodeType>((table) => ({
        id: table.id,
        type: 'table',
        position: { x: table.x, y: table.y },
        data: { table },
      })),
    [tables],
  );

  const edges = useMemo<Edge[]>(
    () =>
      foreignKeys.flatMap((fk) =>
        fk.columnPairs.map((pair, i) => ({
          id: `${fk.id}:${i}`,
          source: fk.fromTable,
          target: fk.toTable,
          sourceHandle: pair.from,
          targetHandle: pair.to,
          label: i === 0 ? fk.onDelete : undefined,
          markerEnd: { type: MarkerType.ArrowClosed },
        })),
      ),
    [foreignKeys],
  );

  const onNodesChange = useCallback(
    (changes: NodeChange[]) => {
      for (const change of changes) {
        if (change.type === 'position' && change.position) {
          setTablePosition(change.id, change.position.x, change.position.y);
        }
      }
    },
    [setTablePosition],
  );

  const onConnect = useCallback(
    (connection: Connection) => {
      const { source, target, sourceHandle, targetHandle } = connection;
      if (source && target && sourceHandle && targetHandle) {
        addForeignKey({
          fromTable: source,
          fromColumn: sourceHandle,
          toTable: target,
          toColumn: targetHandle,
        });
      }
    },
    [addForeignKey],
  );

  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      nodeTypes={nodeTypes}
      onNodesChange={onNodesChange}
      onConnect={onConnect}
      onNodeClick={(_, node) => selectTable(node.id)}
      onPaneClick={() => selectTable(null)}
      fitView
    >
      <Background />
      <Controls />
    </ReactFlow>
  );
}

export default function App() {
  return (
    <div className="app">
      <Toolbar />
      <div className="app__body">
        <ReactFlowProvider>
          <div className="app__canvas">
            <Canvas />
          </div>
        </ReactFlowProvider>
        <RightPanel />
      </div>
    </div>
  );
}
