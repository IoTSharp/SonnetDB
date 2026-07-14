# SonnetDB for VS Code

Connect to SonnetDB, explore every supported data model, run SQL, inspect query results, and use SonnetDB Copilot without leaving VS Code.

[Install from Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=iotsharp.sonnetdb-vscode)

## What you can do

- Save remote SonnetDB connections and switch between databases.
- Browse measurements, relational tables, document collections, KV keyspaces, vector indexes, full-text indexes, MQ topics, and object buckets.
- Run the SQL statement under the cursor or execute the current selection.
- Inspect results as a table, raw JSON, a chart, or a geographic trajectory.
- Use schema-aware completion, diagnostics, hover help, function signatures, quick fixes, and `EXPLAIN`.
- Preview Document, KV, vector, full-text, MQ, and object data through read-only tools.
- Ask SonnetDB Copilot about the current query with read-only access by default.
- Start and stop a managed local SonnetDB Server for a selected data directory.

## Requirements

- Visual Studio Code 1.100 or later.
- A running SonnetDB Server and its base URL, such as `http://127.0.0.1:5080`.
- A bearer token when authentication is enabled on the Server.
- .NET 10 for full C# parser diagnostics. Connections, Explorer, SQL execution, completion, hover, and lightweight diagnostics remain available without it.

## Connect to SonnetDB

1. Open the SonnetDB icon in the Activity Bar.
2. Select **Add Connection**.
3. Enter a connection name, the SonnetDB Server base URL, and a bearer token when required.
4. Run **SonnetDB: Test Connection** to verify the Server and setup state.
5. Select the connection, then choose a database from the Explorer or run **SonnetDB: Select Database**.

Connection names and URLs are stored in VS Code global state. Bearer tokens are stored separately in VS Code `SecretStorage`, never in workspace settings.

## Run a query

1. Run **SonnetDB: New Query** or open a `.sql` file.
2. Write a SonnetDB SQL statement.
3. Press `Ctrl+Enter` on Windows/Linux or `Cmd+Enter` on macOS to run the statement under the cursor.
4. Select SQL and press `Ctrl+Shift+Enter` on Windows/Linux or `Cmd+Shift+Enter` on macOS to run only the selection.

The Query Result editor opens beside the SQL editor and is reused for later queries. Use its result tabs to switch views:

| View | Best for |
|------|----------|
| **Table** | Scanning rows and columns and exporting CSV. |
| **Raw** | Inspecting the complete JSON-shaped response and exporting JSON. |
| **Chart** | Plotting numeric values against a time or category column. |
| **Trajectory** | Viewing GEOPOINT results as ordered, grouped tracks. |

The **Trajectory** tab appears automatically when results contain GeoJSON Point values, `POINT(lat, lon)`, coordinate arrays, or common latitude/longitude objects. A time column determines point order, while a low-cardinality device or tag column separates tracks when available.

Run **SonnetDB: Show Query History** to reopen recent local queries. Use **SonnetDB: Explain Current Statement** to inspect a statement before tuning it.

## Explore every data model

Expand the active database in the SonnetDB Explorer. Object context menus provide the actions that apply to each model:

- **Measurements and tables**: inspect columns, copy names, and open an object in a query editor.
- **Document collections**: filter, project, and sort documents; page with cursors; export JSON or JSONL.
- **KV keyspaces**: scan a read-only preview of keys and values.
- **Vector indexes**: run a top-K vector search preview.
- **Full-text indexes**: preview search results or inspect analyzer output.
- **MQ topics**: browse messages and inspect lag, retention, and dead-letter status.
- **Object buckets**: list objects and inspect object metadata.

Run **SonnetDB: Show Runtime Monitor** from a database or MQ topic to open a read-only snapshot of instance and messaging health.

## SQL assistance

SQL files receive SonnetDB keywords, schema-aware completion, hover documentation, function signature help, delimiter quick fixes, and parser diagnostics.

The extension includes a portable SonnetDB Language Server that uses the same C# SQL parser as the database engine. It only receives SQL text for validation; connection tokens and database paths are not sent to it. If .NET 10 or the sidecar is unavailable, the extension automatically keeps its lightweight TypeScript diagnostics.

Relevant settings:

| Setting | Default | Purpose |
|---------|---------|---------|
| `sonnetdb.languageServer.enabled` | `true` | Enable full parser diagnostics when available. |
| `sonnetdb.languageServer.path` | empty | Use a specific Language Server executable, DLL, or project. |
| `sonnetdb.query.maxRows` | `1000` | Limit rows shown in client-side preview surfaces. |

## Use Copilot safely

Run **SonnetDB: Open Copilot** or **SonnetDB: Ask Copilot About Current Query**. The panel shows model, knowledge-base, citation, and streaming status.

Copilot starts in `read-only` mode. Switching to `read-write` requires an explicit confirmation, and the Server still enforces the permissions of the current credential. Bulk import also shows the target and payload size in a native confirmation dialog before data is sent.

## Use a managed local Server

Run **SonnetDB: Start Managed Local Server**, choose a SonnetDB data directory, and select a Server executable, DLL, or project if auto-discovery cannot find one. The local Server then appears through the same Explorer and query experience as a remote connection.

Use these commands to manage it:

- **SonnetDB: Start Managed Local Server**
- **SonnetDB: Stop Managed Local Server**
- **SonnetDB: Show Managed Server Output**

Relevant settings:

| Setting | Default | Purpose |
|---------|---------|---------|
| `sonnetdb.managedLocal.baseUrl` | `http://127.0.0.1:5080` | HTTP address used by the managed Server. |
| `sonnetdb.managedLocal.serverPath` | empty | Path to a SonnetDB executable, DLL, or project. |
| `sonnetdb.managedLocal.keepRunningOnExit` | `false` | Keep an extension-owned Server running after VS Code exits. |

## Common commands

Open the Command Palette and type `SonnetDB` to find all commands.

| Command | Purpose |
|---------|---------|
| `SonnetDB: Add Connection` | Save a remote Server profile. |
| `SonnetDB: Select Connection` | Change the active Server. |
| `SonnetDB: Select Database` | Change the active database. |
| `SonnetDB: New Query` | Open a SonnetDB SQL editor. |
| `SonnetDB: Run Current Statement` | Execute the statement under the cursor. |
| `SonnetDB: Run Selection` | Execute selected SQL. |
| `SonnetDB: Explain Current Statement` | Inspect the current query plan. |
| `SonnetDB: Show Query History` | Open recent local query history. |
| `SonnetDB: Open Copilot` | Open the SonnetDB Copilot panel. |
| `SonnetDB: Open SQL Reference` | Open the SonnetDB SQL reference. |

## Troubleshooting

**The connection test fails**

Confirm that the base URL points to the SonnetDB Server root, the Server is running, and the bearer token is current. If the Server reports that setup is incomplete, finish first-run setup before selecting a database.

**Explorer is empty or queries have no target database**

Run **SonnetDB: Select Connection**, then **SonnetDB: Select Database**, and refresh the Explorer.

**Full SQL diagnostics are unavailable**

Install the .NET 10 runtime and reload VS Code. You can also inspect `sonnetdb.languageServer.enabled` and `sonnetdb.languageServer.path`. Query execution and lightweight diagnostics continue to work while the sidecar is unavailable.

**The managed local Server does not start**

Check **SonnetDB: Show Managed Server Output**, verify `sonnetdb.managedLocal.serverPath`, and make sure the configured local URL is not already used by another process.

**The Trajectory tab does not appear**

Return a recognized geographic value in the query result. GeoJSON Point, `POINT(lat, lon)`, a two-number coordinate array, or an object with common latitude/longitude keys are supported.

## Links

- [SonnetDB repository](https://github.com/IoTSharp/SonnetDB)
- [Documentation](https://github.com/IoTSharp/SonnetDB/tree/main/docs)
- [Report an issue](https://github.com/IoTSharp/SonnetDB/issues)
- [Visual Studio Marketplace](https://marketplace.visualstudio.com/items?itemName=iotsharp.sonnetdb-vscode)
