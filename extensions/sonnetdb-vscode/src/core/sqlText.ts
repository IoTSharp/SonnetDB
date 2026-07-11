export function extractStatementAtOffset(source: string, offset: number): string | undefined {
  const boundaries = statementBoundaries(source);
  const clampedOffset = Math.max(0, Math.min(offset, source.length));
  const boundary = boundaries.find(([start, end]) =>
    clampedOffset >= start && (clampedOffset < end || end === source.length))
    ?? boundaries.at(-1);
  if (!boundary) {
    return undefined;
  }
  const sql = source.slice(boundary[0], boundary[1]).trim();
  return sql || undefined;
}

function statementBoundaries(source: string): Array<[number, number]> {
  const result: Array<[number, number]> = [];
  let start = 0;
  let quote: "'" | '"' | '`' | null = null;
  let lineComment = false;
  let blockComment = false;

  for (let index = 0; index < source.length; index += 1) {
    const current = source[index];
    const next = source[index + 1];

    if (lineComment) {
      if (current === '\n') {
        lineComment = false;
      }
      continue;
    }
    if (blockComment) {
      if (current === '*' && next === '/') {
        blockComment = false;
        index += 1;
      }
      continue;
    }
    if (quote) {
      if (current === quote) {
        if (next === quote) {
          index += 1;
        } else {
          quote = null;
        }
      }
      continue;
    }
    if (current === '-' && next === '-') {
      lineComment = true;
      index += 1;
      continue;
    }
    if (current === '/' && next === '*') {
      blockComment = true;
      index += 1;
      continue;
    }
    if (current === "'" || current === '"' || current === '`') {
      quote = current;
      continue;
    }
    if (current === ';') {
      result.push([start, index + 1]);
      start = index + 1;
    }
  }

  if (start < source.length) {
    result.push([start, source.length]);
  }
  return result.filter(([left, right]) => source.slice(left, right).trim().length > 0);
}
