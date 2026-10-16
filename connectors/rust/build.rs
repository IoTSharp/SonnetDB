use std::env;
use std::path::PathBuf;

fn main() {
    println!("cargo:rerun-if-env-changed=SONNETDB_NATIVE_LIB_DIR");

    let target_os = env::var("CARGO_CFG_TARGET_OS").unwrap_or_default();
    if let Ok(native_dir) = env::var("SONNETDB_NATIVE_LIB_DIR") {
        println!("cargo:rustc-link-search=native={native_dir}");
    } else if let Some(default_dir) = default_native_dir(&target_os) {
        if default_dir.exists() {
            println!("cargo:rustc-link-search=native={}", default_dir.display());
        } else {
            println!(
                "cargo:warning=SonnetDB native library directory was not found at {}. Set SONNETDB_NATIVE_LIB_DIR.",
                default_dir.display()
            );
        }
    }

    match target_os.as_str() {
        "windows" => println!("cargo:rustc-link-lib=dylib=SonnetDB.Native"),
        "linux" => {
            // SonnetDB.Native.so is published without a lib* prefix, so use the
            // supported verbatim modifier to request the linker's exact-file form.
            println!("cargo:rustc-link-lib=dylib:+verbatim=SonnetDB.Native.so");
        }
        other => panic!("SonnetDB Rust connector currently supports windows and linux, not {other}."),
    }
}

fn default_native_dir(target_os: &str) -> Option<PathBuf> {
    let manifest_dir = PathBuf::from(env::var("CARGO_MANIFEST_DIR").ok()?);
    match target_os {
        "windows" => {
            Some(manifest_dir.join("../../embedded/native/SonnetDB.Native/bin/Release/net10.0/win-x64/native"))
        }
        "linux" => Some(manifest_dir.join("../../artifacts/connectors/c/linux-x64")),
        _ => None,
    }
}
