package com.sonnetdb.examples;

import com.sonnetdb.SonnetDbConnection;
import com.sonnetdb.SonnetDbKeyValueStore;
import com.sonnetdb.SonnetDbKvCasResult;
import com.sonnetdb.SonnetDbKvEntry;
import com.sonnetdb.SonnetDbResult;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

/**
 * SonnetDB Java connector quickstart.
 */
public final class Quickstart {
    private Quickstart() {
    }

    public static void main(String[] args) throws IOException {
        Path dataDir = Files.createTempDirectory("sonnetdb-java-quickstart-");
        run(dataDir);
        System.out.println("data directory: " + dataDir);
    }

    private static void run(Path dataDir) {
        System.out.println("SonnetDB native version: " + SonnetDbConnection.version());

        try (SonnetDbConnection connection = SonnetDbConnection.open(dataDir.toString())) {
            connection.executeNonQuery("CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
            int inserted = connection.executeNonQuery(
                "INSERT INTO cpu (time, host, usage) VALUES " +
                    "(1710000000000, 'edge-1', 0.42)," +
                    "(1710000001000, 'edge-1', 0.73)");
            System.out.println("inserted rows: " + inserted);

            try (SonnetDbKeyValueStore kv = connection.openKeyValueStore("app-cache", "quickstart")) {
                long version = kv.set("device:edge-1", "online".getBytes(StandardCharsets.UTF_8));
                SonnetDbKvEntry entry = kv.get("device:edge-1");
                if (entry != null) {
                    System.out.println("kv " + entry.key() + " = "
                        + new String(entry.value(), StandardCharsets.UTF_8)
                        + " (version " + entry.version() + ")");
                }

                long[] counter = kv.increment("counter", 3);
                System.out.println("kv counter: " + counter[0] + " (version " + counter[1] + ")");

                SonnetDbKvCasResult cas = kv.compareAndSet(
                    "device:edge-1",
                    version,
                    "offline".getBytes(StandardCharsets.UTF_8));
                System.out.println("kv cas swapped: " + cas.swapped()
                    + " (current " + cas.currentVersion() + ", new " + cas.newVersion() + ")");

                for (SonnetDbKvEntry row : kv.scanPrefix("device:", 10)) {
                    System.out.println("kv scan " + row.key() + " = "
                        + new String(row.value(), StandardCharsets.UTF_8));
                }
            }

            try (SonnetDbResult result = connection.execute(
                "SELECT time, host, usage FROM cpu WHERE host = 'edge-1' LIMIT 10")) {
                for (int i = 0; i < result.columnCount(); i++) {
                    if (i > 0) {
                        System.out.print("\t");
                    }
                    System.out.print(result.columnName(i));
                }
                System.out.println();

                while (result.next()) {
                    System.out.printf(
                        "%d\t%s\t%.3f%n",
                        result.getLong(0),
                        result.getString(1),
                        result.getDouble(2));
                }
            }
        }
    }

}
