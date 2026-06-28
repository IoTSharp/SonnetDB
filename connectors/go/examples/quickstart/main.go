//go:build cgo && (windows || linux)

package main

import (
	"fmt"
	"log"
	"os"

	sonnetdb "github.com/sonnetdb/sonnetdb/connectors/go"
)

func main() {
	dataDir, err := os.MkdirTemp("", "sonnetdb-go-quickstart-")
	if err != nil {
		log.Fatal(err)
	}

	version, err := sonnetdb.Version()
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println("SonnetDB native version:", version)

	connection, err := sonnetdb.Open(dataDir)
	if err != nil {
		log.Fatal(err)
	}
	defer connection.Close()

	if _, err := connection.ExecuteNonQuery("CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)"); err != nil {
		log.Fatal(err)
	}

	inserted, err := connection.ExecuteNonQuery(
		"INSERT INTO cpu (time, host, usage) VALUES " +
			"(1710000000000, 'edge-1', 0.42)," +
			"(1710000001000, 'edge-1', 0.73)")
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println("inserted rows:", inserted)

	kv, err := connection.OpenKV("app-cache", "quickstart")
	if err != nil {
		log.Fatal(err)
	}
	defer kv.Close()

	version, err := kv.Set("device:edge-1", []byte("online"))
	if err != nil {
		log.Fatal(err)
	}
	entry, err := kv.Get("device:edge-1")
	if err != nil {
		log.Fatal(err)
	}
	if entry != nil {
		fmt.Printf("kv %s = %s (version %d)\n", entry.Key, string(entry.Value), entry.Version)
	}

	counter, counterVersion, err := kv.Incr("counter", 3)
	if err != nil {
		log.Fatal(err)
	}
	fmt.Printf("kv counter: %d (version %d)\n", counter, counterVersion)

	cas, err := kv.CAS("device:edge-1", version, []byte("offline"))
	if err != nil {
		log.Fatal(err)
	}
	fmt.Printf("kv cas swapped: %t (current %d, new %d)\n", cas.Swapped, cas.CurrentVersion, cas.NewVersion)

	entries, err := kv.ScanPrefix("device:", 10)
	if err != nil {
		log.Fatal(err)
	}
	for _, row := range entries {
		fmt.Printf("kv scan %s = %s\n", row.Key, string(row.Value))
	}

	result, err := connection.Execute("SELECT time, host, usage FROM cpu WHERE host = 'edge-1' LIMIT 10")
	if err != nil {
		log.Fatal(err)
	}
	defer result.Close()

	columns, err := result.Columns()
	if err != nil {
		log.Fatal(err)
	}
	for i, column := range columns {
		if i > 0 {
			fmt.Print("\t")
		}
		fmt.Print(column)
	}
	fmt.Println()

	for {
		ok, err := result.Next()
		if err != nil {
			log.Fatal(err)
		}
		if !ok {
			break
		}

		timestamp, err := result.Int64(0)
		if err != nil {
			log.Fatal(err)
		}
		host, _, err := result.Text(1)
		if err != nil {
			log.Fatal(err)
		}
		usage, err := result.Double(2)
		if err != nil {
			log.Fatal(err)
		}

		fmt.Printf("%d\t%s\t%.3f\n", timestamp, host, usage)
	}

	fmt.Println("data directory:", dataDir)
}
