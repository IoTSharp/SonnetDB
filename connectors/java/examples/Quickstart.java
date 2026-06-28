package com.sonnetdb.examples;

import com.sonnetdb.SonnetDbBulkOptions;
import com.sonnetdb.SonnetDbConnection;
import com.sonnetdb.SonnetDbDocumentCollection;
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

            int bulkRows = connection.executeBulk(
                "ignored,host=edge-2 usage=0.81 1710000002000\n" +
                    "ignored,host=edge-2 usage=0.86 1710000003000",
                new SonnetDbBulkOptions("cpu", "failfast", "false"));
            System.out.println("bulk rows: " + bulkRows);

            try (SonnetDbDocumentCollection documents = connection.openDocumentCollection("devices")) {
                System.out.println("doc create: " + documents.createCollection("{\"ifNotExists\":true}"));
                System.out.println("doc insert: " + documents.insert(
                    "{\"documents\":[" +
                        "{\"id\":\"dev-1\",\"document\":{\"site\":\"north\",\"kind\":\"pump\",\"score\":7}}," +
                        "{\"id\":\"dev-2\",\"document\":{\"site\":\"south\",\"kind\":\"fan\",\"score\":3}}" +
                        "],\"ordered\":true}"));
                System.out.println("doc find: " + documents.findPage(
                    "{\"limit\":10,\"filter\":{\"path\":\"$.site\",\"op\":\"eq\",\"value\":\"north\"}}"));
                System.out.println("doc update: " + documents.update(
                    "{\"id\":\"dev-1\",\"update\":{\"set\":{\"$.status\":\"ok\"},\"inc\":{\"$.score\":1}}}"));
                System.out.println("doc aggregate: " + documents.aggregate(
                    "[{\"$match\":{\"path\":\"$.site\",\"op\":\"eq\",\"value\":\"north\"}}," +
                        "{\"$group\":{\"keys\":[{\"name\":\"site\",\"path\":\"$.site\"}]," +
                        "\"accumulators\":[{\"name\":\"rows\",\"op\":\"count\"}," +
                        "{\"name\":\"total\",\"op\":\"sum\",\"path\":\"$.score\"}]}}]"));
                System.out.println("doc delete: " + documents.delete("{\"ids\":[\"dev-2\"],\"ordered\":true}"));
            }

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
