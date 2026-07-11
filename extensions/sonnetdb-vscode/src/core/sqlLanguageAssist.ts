export interface SqlFunctionParameter {
  label: string;
  documentation: string;
}

export interface SqlFunctionSignature {
  name: string;
  label: string;
  documentation: string;
  parameters: SqlFunctionParameter[];
}

export interface SqlFunctionCall {
  signature: SqlFunctionSignature;
  activeParameter: number;
}

export interface SqlRepairSuggestion {
  title: string;
  offset: number;
  length: number;
  text: string;
}

const Signatures = new Map<string, SqlFunctionSignature>([
  signature('knn', ['vector_column', 'query_vector', 'top_k'], 'Runs a top-K vector search.'),
  signature('forecast', ['value', 'horizon', 'confidence'], 'Forecasts future values from a time series.'),
  signature('pid', ['value', 'setpoint', 'kp', 'ki', 'kd'], 'Evaluates a PID control value.'),
  signature('pid_series', ['value', 'setpoint', 'kp', 'ki', 'kd'], 'Evaluates PID control over a time series.'),
  signature('pid_estimate', ['value', 'setpoint'], 'Estimates PID tuning values from a time series.'),
  signature('anomaly', ['value', 'sensitivity'], 'Returns anomaly scores for a time series.'),
  signature('changepoint', ['value', 'sensitivity'], 'Detects structural changes in a time series.'),
  signature('difference', ['value'], 'Returns the difference from the previous value.'),
  signature('delta', ['value'], 'Returns the non-negative difference from the previous value.'),
  signature('increase', ['value'], 'Returns the cumulative increase over the selected range.'),
  signature('derivative', ['value', 'unit'], 'Returns the derivative over the requested time unit.'),
  signature('rate', ['value', 'unit'], 'Returns the average rate of change.'),
  signature('irate', ['value', 'unit'], 'Returns the instantaneous rate of change.'),
  signature('cumulative_sum', ['value'], 'Returns the running sum.'),
  signature('integral', ['value', 'unit'], 'Returns the integral over the requested time unit.'),
  signature('moving_average', ['value', 'window'], 'Returns the moving average for a window.'),
  signature('ewma', ['value', 'alpha'], 'Returns an exponentially weighted moving average.'),
  signature('holt_winters', ['value', 'season_length', 'forecast_horizon'], 'Runs Holt-Winters forecasting.'),
  signature('fill', ['value', 'strategy'], 'Fills missing values using the selected strategy.'),
  signature('locf', ['value'], 'Fills missing values with the last observation.'),
  signature('interpolate', ['value'], 'Linearly interpolates missing values.'),
  signature('state_changes', ['value'], 'Counts state transitions.'),
  signature('state_duration', ['value'], 'Returns the duration of each state.'),
  signature('cosine_distance', ['left_vector', 'right_vector'], 'Returns cosine distance between two vectors.'),
  signature('l2_distance', ['left_vector', 'right_vector'], 'Returns Euclidean distance between two vectors.'),
  signature('inner_product', ['left_vector', 'right_vector'], 'Returns the inner product of two vectors.'),
]);

/**
 * 查找光标所在的 SonnetDB SQL 函数调用，并计算当前参数序号。
 */
export function findSqlFunctionCall(source: string, offset: number): SqlFunctionCall | undefined {
  const frames: Array<{ name?: string; activeParameter: number }> = [];
  const end = Math.max(0, Math.min(offset, source.length));
  let quote: "'" | '"' | '`' | undefined;
  let lineComment = false;
  let blockComment = false;

  for (let index = 0; index < end; index += 1) {
    const current = source[index];
    const next = source[index + 1];
    if (lineComment) {
      if (current === '\n') lineComment = false;
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
        if (next === quote) index += 1;
        else quote = undefined;
      }
      continue;
    }
    if (current === '-' && next === '-') {
      lineComment = true;
      index += 1;
    } else if (current === '/' && next === '*') {
      blockComment = true;
      index += 1;
    } else if (current === "'" || current === '"' || current === '`') {
      quote = current;
    } else if (current === '(') {
      const match = source.slice(0, index).match(/([A-Za-z_][A-Za-z0-9_]*)\s*$/u);
      frames.push({ name: match?.[1].toLowerCase(), activeParameter: 0 });
    } else if (current === ',') {
      const frame = frames.at(-1);
      if (frame) frame.activeParameter += 1;
    } else if (current === ')') {
      frames.pop();
    }
  }

  for (let index = frames.length - 1; index >= 0; index -= 1) {
    const frame = frames[index];
    const functionSignature = frame.name ? Signatures.get(frame.name) : undefined;
    if (functionSignature) {
      return {
        signature: functionSignature,
        activeParameter: Math.min(frame.activeParameter, functionSignature.parameters.length - 1),
      };
    }
  }
  return undefined;
}

/**
 * 将轻量诊断转换为可预览、可撤销的编辑器快速修复。
 */
export function repairSqlDiagnostic(
  message: string,
  offset: number,
  length: number,
  documentLength: number,
): SqlRepairSuggestion | undefined {
  if (message === 'Unmatched opening parenthesis.') {
    return { title: 'Insert closing parenthesis', offset: documentLength, length: 0, text: ')' };
  }
  if (message === 'Unmatched closing parenthesis.') {
    return { title: 'Remove unmatched parenthesis', offset, length, text: '' };
  }
  if (message === 'Unclosed block comment.') {
    return { title: 'Close block comment', offset: documentLength, length: 0, text: '*/' };
  }
  if (message.startsWith('Unclosed ') && message.endsWith(' quote.')) {
    const quote = message.slice('Unclosed '.length, -' quote.'.length);
    if (quote === "'" || quote === '"' || quote === '`') {
      return { title: `Insert closing ${quote} quote`, offset: documentLength, length: 0, text: quote };
    }
  }
  return undefined;
}

function signature(name: string, parameters: string[], documentation: string): [string, SqlFunctionSignature] {
  const definition: SqlFunctionSignature = {
    name,
    label: `${name}(${parameters.join(', ')})`,
    documentation,
    parameters: parameters.map((parameter) => ({
      label: parameter,
      documentation: `${parameter.replaceAll('_', ' ')} parameter.`,
    })),
  };
  return [name, definition];
}
