export interface SqlDiagnosticIssue {
  offset: number;
  length: number;
  message: string;
}

export function scanSqlDiagnostics(source: string): SqlDiagnosticIssue[] {
  const issues: SqlDiagnosticIssue[] = [];
  const parentheses: number[] = [];
  let quote: { value: "'" | '"' | '`'; offset: number } | undefined;
  let lineComment = false;
  let blockCommentOffset: number | undefined;

  for (let index = 0; index < source.length; index += 1) {
    const current = source[index];
    const next = source[index + 1];
    if (lineComment) {
      if (current === '\n') lineComment = false;
      continue;
    }
    if (blockCommentOffset !== undefined) {
      if (current === '*' && next === '/') {
        blockCommentOffset = undefined;
        index += 1;
      }
      continue;
    }
    if (quote) {
      if (current === quote.value) {
        if (next === quote.value) index += 1;
        else quote = undefined;
      }
      continue;
    }
    if (current === '-' && next === '-') {
      lineComment = true;
      index += 1;
    } else if (current === '/' && next === '*') {
      blockCommentOffset = index;
      index += 1;
    } else if (current === "'" || current === '"' || current === '`') {
      quote = { value: current, offset: index };
    } else if (current === '(') {
      parentheses.push(index);
    } else if (current === ')') {
      const open = parentheses.pop();
      if (open === undefined) {
        issues.push({ offset: index, length: 1, message: 'Unmatched closing parenthesis.' });
      }
    }
  }

  if (quote) {
    issues.push({ offset: quote.offset, length: 1, message: `Unclosed ${quote.value} quote.` });
  }
  if (blockCommentOffset !== undefined) {
    issues.push({ offset: blockCommentOffset, length: 2, message: 'Unclosed block comment.' });
  }
  for (const offset of parentheses) {
    issues.push({ offset, length: 1, message: 'Unmatched opening parenthesis.' });
  }
  return issues;
}
