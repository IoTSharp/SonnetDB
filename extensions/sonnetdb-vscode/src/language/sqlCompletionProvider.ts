import * as vscode from 'vscode';
import { SonnetDbClient } from '../core/sonnetdbClient';
import { SchemaResponse, SonnetDbConnectionProfile } from '../core/types';

const Keywords = [
  'SELECT', 'FROM', 'WHERE', 'GROUP BY', 'ORDER BY', 'LIMIT', 'OFFSET', 'HAVING',
  'INSERT INTO', 'VALUES', 'DELETE FROM', 'CREATE MEASUREMENT', 'CREATE TABLE',
  'SHOW MEASUREMENTS', 'SHOW TABLES', 'SHOW INDEXES', 'DESCRIBE MEASUREMENT', 'EXPLAIN',
  'TAG', 'FIELD', 'BUCKET', 'AND', 'OR', 'AS', 'ASC', 'DESC', 'BETWEEN', 'IN', 'LIKE',
];

const Functions = [
  'knn', 'forecast', 'pid', 'pid_series', 'pid_estimate', 'anomaly', 'changepoint',
  'difference', 'delta', 'increase', 'derivative', 'rate', 'irate', 'cumulative_sum',
  'integral', 'moving_average', 'ewma', 'holt_winters', 'fill', 'locf', 'interpolate',
  'state_changes', 'state_duration', 'cosine_distance', 'l2_distance', 'inner_product',
];

export function registerSqlCompletionProvider(
  context: vscode.ExtensionContext,
  getActiveProfile: () => SonnetDbConnectionProfile | undefined,
  createClient: (profile: SonnetDbConnectionProfile) => Promise<SonnetDbClient>,
): void {
  let cache: { key: string; expires: number; schema: SchemaResponse } | undefined;
  const provider = vscode.languages.registerCompletionItemProvider(
    [{ language: 'sql' }, { language: 'sonnetdb-sql' }],
    {
      provideCompletionItems: async () => {
        const items = [
          ...Keywords.map((keyword) => completion(keyword, vscode.CompletionItemKind.Keyword, 'SonnetDB SQL keyword')),
          ...Functions.map((name) => completion(name, vscode.CompletionItemKind.Function, 'SonnetDB SQL function')),
        ];

        const profile = getActiveProfile();
        if (!profile?.defaultDatabase) {
          return items;
        }
        const key = `${profile.id}/${profile.defaultDatabase}`;
        try {
          if (!cache || cache.key !== key || cache.expires < Date.now()) {
            cache = {
              key,
              expires: Date.now() + 30_000,
              schema: await (await createClient(profile)).fetchSchema(profile.defaultDatabase),
            };
          }
          for (const measurement of cache.schema.measurements) {
            items.push(completion(measurement.name, vscode.CompletionItemKind.Class, 'Measurement'));
            for (const column of measurement.columns) {
              items.push(completion(column.name, vscode.CompletionItemKind.Field, `${column.role} · ${column.dataType}`));
            }
          }
          for (const table of cache.schema.tables ?? []) {
            items.push(completion(table.name, vscode.CompletionItemKind.Struct, 'Table'));
            for (const column of table.columns) {
              items.push(completion(column.name, vscode.CompletionItemKind.Field, column.dataType));
            }
          }
        } catch {
          // 关键字补全不应因 schema 暂时不可用而失败。
        }
        return items;
      },
    },
  );
  context.subscriptions.push(provider);
}

function completion(label: string, kind: vscode.CompletionItemKind, detail: string): vscode.CompletionItem {
  const item = new vscode.CompletionItem(label, kind);
  item.detail = detail;
  return item;
}
