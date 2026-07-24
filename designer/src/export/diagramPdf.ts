import type { jsPDF } from 'jspdf';
import { useDesigner, type EditorTable } from '../state/store';

// Deterministic vector rendering of the ER diagram straight into a PDF. Drawing
// from the model (not screenshotting the DOM) keeps edges and labels reliable.

const NODE_W = 230;
const HEADER_H = 32;
const ROW_H = 24;
const PADDING = 48;
const ARROW = 8;

const COLORS = {
  page: '#0f1420',
  node: '#1f2738',
  header: '#4f8cff',
  border: '#2b3550',
  text: '#e6ebf5',
  muted: '#8a97b1',
  edge: '#e0862f',
  headerText: '#ffffff',
};

const hex = (value: string): [number, number, number] => [
  parseInt(value.slice(1, 3), 16),
  parseInt(value.slice(3, 5), 16),
  parseInt(value.slice(5, 7), 16),
];

const nodeHeight = (table: EditorTable): number =>
  HEADER_H + Math.max(table.columns.length, 1) * ROW_H;

const truncate = (text: string, max = 24): string =>
  text.length > max ? `${text.slice(0, max - 1)}…` : text;

/** Renders the current design as a vector ER-diagram PDF and downloads it. */
export async function exportDiagramPdf(): Promise<void> {
  const { schemaName, tables, foreignKeys } = useDesigner.getState();
  if (tables.length === 0) {
    throw new Error('Diagramma bo‘sh — avval jadval qo‘shing.');
  }

  // Index every column so foreign keys can locate exact row anchor points.
  const location = new Map<string, { table: EditorTable; row: number }>();
  for (const table of tables) {
    table.columns.forEach((column, row) => location.set(column.id, { table, row }));
  }

  const minX = Math.min(...tables.map((t) => t.x));
  const minY = Math.min(...tables.map((t) => t.y));
  const maxX = Math.max(...tables.map((t) => t.x + NODE_W));
  const maxY = Math.max(...tables.map((t) => t.y + nodeHeight(t)));
  const width = Math.ceil(maxX - minX + PADDING * 2);
  const height = Math.ceil(maxY - minY + PADDING * 2);
  const ox = PADDING - minX;
  const oy = PADDING - minY;

  const { jsPDF: JsPdf } = await import('jspdf');
  const pdf = new JsPdf({
    orientation: width >= height ? 'landscape' : 'portrait',
    unit: 'px',
    format: [width, height],
  });
  pdf.setFont('helvetica', 'normal');

  // Background.
  pdf.setFillColor(...hex(COLORS.page));
  pdf.rect(0, 0, width, height, 'F');

  drawEdges(pdf, foreignKeys, location, ox, oy);
  for (const table of tables) {
    drawTable(pdf, table, ox, oy);
  }

  pdf.save(`${schemaName}.pdf`);
}

function drawTable(pdf: jsPDF, table: EditorTable, ox: number, oy: number): void {
  const x = table.x + ox;
  const y = table.y + oy;
  const h = nodeHeight(table);

  // Body.
  pdf.setFillColor(...hex(COLORS.node));
  pdf.setDrawColor(...hex(COLORS.border));
  pdf.roundedRect(x, y, NODE_W, h, 6, 6, 'FD');

  // Header.
  pdf.setFillColor(...hex(COLORS.header));
  pdf.roundedRect(x, y, NODE_W, HEADER_H, 6, 6, 'F');
  pdf.rect(x, y + HEADER_H / 2, NODE_W, HEADER_H / 2, 'F');
  pdf.setTextColor(...hex(COLORS.headerText));
  pdf.setFont('helvetica', 'bold');
  pdf.setFontSize(11);
  const title = table.schema ? `${table.schema}.${table.name}` : table.name;
  pdf.text(truncate(title, 26), x + 12, y + HEADER_H / 2, { baseline: 'middle' });

  // Columns.
  pdf.setFontSize(10);
  table.columns.forEach((column, i) => {
    const rowY = y + HEADER_H + i * ROW_H + ROW_H / 2;
    pdf.setFont('helvetica', column.isPrimaryKey ? 'bold' : 'normal');
    pdf.setTextColor(...hex(COLORS.text));
    const name = column.isPrimaryKey ? `${column.name} *` : column.name;
    pdf.text(truncate(name, 20), x + 12, rowY, { baseline: 'middle' });

    pdf.setFont('helvetica', 'normal');
    pdf.setTextColor(...hex(COLORS.muted));
    const type = column.dataType + (column.nullable ? '?' : '');
    pdf.text(type, x + NODE_W - 12, rowY, { baseline: 'middle', align: 'right' });
  });
}

function drawEdges(
  pdf: jsPDF,
  foreignKeys: ReturnType<typeof useDesigner.getState>['foreignKeys'],
  location: Map<string, { table: EditorTable; row: number }>,
  ox: number,
  oy: number,
): void {
  pdf.setDrawColor(...hex(COLORS.edge));
  pdf.setFillColor(...hex(COLORS.edge));
  pdf.setLineWidth(1.4);

  for (const fk of foreignKeys) {
    for (let i = 0; i < fk.columnPairs.length; i++) {
      const pair = fk.columnPairs[i];
      const from = location.get(pair.from);
      const to = location.get(pair.to);
      if (!from || !to) {
        continue;
      }

      const fromRight = to.table.x >= from.table.x;
      const sx = from.table.x + ox + (fromRight ? NODE_W : 0);
      const sy = from.table.y + oy + HEADER_H + from.row * ROW_H + ROW_H / 2;
      const toRight = to.table.x + NODE_W <= from.table.x;
      const tx = to.table.x + ox + (toRight ? NODE_W : 0);
      const ty = to.table.y + oy + HEADER_H + to.row * ROW_H + ROW_H / 2;

      pdf.line(sx, sy, tx, ty);
      drawArrow(pdf, sx, sy, tx, ty);

      if (i === 0) {
        drawLabel(pdf, fk.onDelete, (sx + tx) / 2, (sy + ty) / 2);
      }
    }
  }
}

function drawArrow(pdf: jsPDF, sx: number, sy: number, tx: number, ty: number): void {
  const angle = Math.atan2(ty - sy, tx - sx);
  const left = angle + Math.PI - 0.4;
  const right = angle + Math.PI + 0.4;
  pdf.triangle(
    tx,
    ty,
    tx + ARROW * Math.cos(left),
    ty + ARROW * Math.sin(left),
    tx + ARROW * Math.cos(right),
    ty + ARROW * Math.sin(right),
    'F',
  );
}

function drawLabel(pdf: jsPDF, text: string, cx: number, cy: number): void {
  pdf.setFontSize(9);
  const w = pdf.getTextWidth(text) + 8;
  const h = 14;
  pdf.setFillColor(...hex(COLORS.node));
  pdf.setDrawColor(...hex(COLORS.border));
  pdf.roundedRect(cx - w / 2, cy - h / 2, w, h, 3, 3, 'FD');
  pdf.setTextColor(...hex(COLORS.muted));
  pdf.text(text, cx, cy, { baseline: 'middle', align: 'center' });
  pdf.setDrawColor(...hex(COLORS.edge));
}
