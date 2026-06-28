use sonnetdb::{BulkOptions, Connection};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let data_dir = std::env::temp_dir().join(format!(
        "sonnetdb-rust-quickstart-{}",
        std::process::id()
    ));

    println!("SonnetDB native version: {}", sonnetdb::version()?);

    let connection = Connection::open_path(&data_dir)?;
    connection.execute_non_query("CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)")?;

    let inserted = connection.execute_non_query(
        "INSERT INTO cpu (time, host, usage) VALUES \
         (1710000000000, 'edge-1', 0.42),\
         (1710000001000, 'edge-1', 0.73)",
    )?;
    println!("inserted rows: {inserted}");

    let bulk = connection.execute_bulk(
        "ignored,host=edge-2 usage=0.81 1710000002000\n\
         ignored,host=edge-2 usage=0.86 1710000003000",
        Some(&BulkOptions {
            measurement: Some("cpu".to_string()),
            on_error: Some("failfast".to_string()),
            flush: Some("false".to_string()),
        }),
    )?;
    println!("bulk rows: {bulk}");

    let documents = connection.open_document_collection("devices")?;
    println!(
        "doc create: {}",
        documents.create_collection(Some("{\"ifNotExists\":true}"))?
    );
    println!(
        "doc insert: {}",
        documents.insert("{\"documents\":[{\"id\":\"dev-1\",\"document\":{\"site\":\"north\",\"kind\":\"pump\",\"score\":7}},{\"id\":\"dev-2\",\"document\":{\"site\":\"south\",\"kind\":\"fan\",\"score\":3}}],\"ordered\":true}")?
    );
    println!(
        "doc find: {}",
        documents.find_page(Some("{\"limit\":10,\"filter\":{\"path\":\"$.site\",\"op\":\"eq\",\"value\":\"north\"}}"))?
    );
    println!(
        "doc update: {}",
        documents.update("{\"id\":\"dev-1\",\"update\":{\"set\":{\"$.status\":\"ok\"},\"inc\":{\"$.score\":1}}}")?
    );
    println!(
        "doc aggregate: {}",
        documents.aggregate("[{\"$match\":{\"path\":\"$.site\",\"op\":\"eq\",\"value\":\"north\"}},{\"$group\":{\"keys\":[{\"name\":\"site\",\"path\":\"$.site\"}],\"accumulators\":[{\"name\":\"rows\",\"op\":\"count\"},{\"name\":\"total\",\"op\":\"sum\",\"path\":\"$.score\"}]}}]")?
    );
    println!(
        "doc delete: {}",
        documents.delete("{\"ids\":[\"dev-2\"],\"ordered\":true}")?
    );

    let kv = connection.open_kv("app-cache", Some("quickstart"))?;
    let version = kv.set("device:edge-1", b"online", None)?;
    if let Some(entry) = kv.get("device:edge-1")? {
        println!(
            "kv {} = {} (version {})",
            entry.key,
            String::from_utf8_lossy(&entry.value),
            entry.version
        );
    }
    let (counter, counter_version) = kv.incr("counter", 3)?;
    println!("kv counter: {counter} (version {counter_version})");
    let cas = kv.compare_and_set("device:edge-1", version, b"offline", None)?;
    println!(
        "kv cas swapped: {} (current {}, new {})",
        cas.swapped, cas.current_version, cas.new_version
    );
    for row in kv.scan_prefix("device:", 10)? {
        println!("kv scan {} = {}", row.key, String::from_utf8_lossy(&row.value));
    }

    let mut result =
        connection.execute("SELECT time, host, usage FROM cpu WHERE host = 'edge-1' LIMIT 10")?;
    println!("{}", result.columns()?.join("\t"));

    while result.next()? {
        let timestamp = result.get_i64(0)?;
        let host = result.get_text(1)?.unwrap_or_default();
        let usage = result.get_f64(2)?;
        println!("{timestamp}\t{host}\t{usage:.3}");
    }

    println!("data directory: {}", data_dir.display());
    Ok(())
}
