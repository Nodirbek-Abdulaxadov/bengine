import { useCallback, useMemo, useState } from 'react';
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
import { Toasts } from './components/Toasts';
import { Toolbar } from './components/Toolbar';
import { Explorer } from './components/Explorer';
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
          markerEnd: { type: MarkerType.ArrowClosed, color: '#e0862f' },
          // Explicit inline colors so the diagram survives PNG/PDF capture,
          // which cannot resolve React Flow's CSS-variable edge styling.
          style: { stroke: '#e0862f', strokeWidth: 1.5 },
          labelStyle: { fill: '#e6ebf5', fontSize: 11 },
          labelBgStyle: { fill: '#1f2738' },
          labelBgPadding: [6, 3] as [number, number],
          labelBgBorderRadius: 4,
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

type View = 'designer' | 'explorer';

const tabStyle = (active: boolean): React.CSSProperties => ({
  background: active ? '#e0862f' : 'transparent',
  color: active ? '#1a1206' : '#e6ebf5',
  border: '1px solid #2e3a4b',
  borderRadius: 6,
  padding: '4px 12px',
  cursor: 'pointer',
  fontWeight: 600,
});

export default function App() {
  const [view, setViewState] = useState<View>(() => {
    try {
      return localStorage.getItem('bengine.view') === 'explorer' ? 'explorer' : 'designer';
    } catch {
      return 'designer';
    }
  });
  const setView = (next: View) => {
    setViewState(next);
    try {
      localStorage.setItem('bengine.view', next);
    } catch {
      // Storage can be unavailable (private mode); the choice just isn't remembered.
    }
  };

  return (
    <div className="app">
      <div
        style={{
          display: 'flex',
          gap: 8,
          alignItems: 'center',
          padding: '8px 10px',
          background: '#11161e',
          borderBottom: '1px solid #2e3a4b',
        }}
      >
        <strong style={{ color: '#e6ebf5', marginRight: 8 }}>Bengine</strong>
        <button style={tabStyle(view === 'designer')} onClick={() => setView('designer')}>
          Designer
        </button>
        <button style={tabStyle(view === 'explorer')} onClick={() => setView('explorer')}>
          Database Explorer
        </button>
      </div>

      {view === 'designer' ? (
        <>
          <Toolbar />
          <div className="app__body">
            <ReactFlowProvider>
              <div className="app__canvas">
                <Canvas />
              </div>
            </ReactFlowProvider>
            <RightPanel />
          </div>
        </>
      ) : (
        <div className="app__body">
          <Explorer />
        </div>
      )}

      <Toasts />
    </div>
  );
}
