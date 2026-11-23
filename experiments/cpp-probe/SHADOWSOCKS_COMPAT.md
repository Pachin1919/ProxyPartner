# Native Shadowsocks YAML compatibility

Base900490789318d3b8b18f354ddf5855cf7508406e. Own only SS adapter, SS YAML routing and SS build imports. No NativeDesktop changes. Original ReadShadowsocks scope was read from build/DesktopBaseline/IndependentNodeProbe.cs blob4999816b1bb0b7e2ba2353009dee640cadde5a44: aes-128-gcm/aes-192-gcm/aes-256-gcm/chacha20-ietf-poly1305, password1..4096 UTF16 units (up to12288 UTF8 bytes), either no plugin/options or plugin=obfs + mode=tls. Missing obfs host defaults bing.com; explicitly empty/invalid ASCII host rejects, port suffix is retained. Orphan options, HTTP-obfs, other plugins/ciphers/unknown fields reject. Malformed nonscalar obfs host rejects rather than treating invalid data as a missing label. Existing C ABI cannot carry NUL by itself; new V2 uses explicit password byte count and YAML SS alone permits NUL. Other protocols' password restrictions are unchanged.

Existing exports/signatures are unchanged: ProbeInitialize,ProbeShutdown,ProbeWebsite,ProbeEcho. Legacy website/echo wrappers still mean ChaCha20+TLS-obfs. New additive Cdecl exports:

```cpp
int ProbeShadowsocksWebsiteV2(const char* address,const char* port,const char* password,
    unsigned passwordBytes,const char* cipher,const char* obfsHost,const char* host,const char* path,
    const char* headers,unsigned remaining,void* stop,int* status);
int ProbeShadowsocksEchoV2(const char* address,const char* port,const char* password,
    unsigned passwordBytes,const char* cipher,const char* obfsHost,const char* host,const char* path,
    const char* headers,unsigned remaining,void* stop,int* status,
    char* body,unsigned capacity,unsigned* used);
```

Strings/pointers nonnull and UTF8; password is a buffer of exactly passwordBytes(1..12288; CLI enforces1..4096 UTF16 units), so embedded NUL survives. Empty obfsHost means standard SS, otherwise TLS-obfs. Echo headers must be empty and body capacity>=65. Old status/body semantics, certificate+name verification, remaining deadline and cancellation retained. CLI prefers V2; if only legacy DLL exports exist it permits only prior non-NUL ChaCha20+TLS-obfs configurations. New AES/plain/NUL configurations never silently fall back to the old mode. No JSONL/schema change.

Engine reuses existing EVP_BytesToKey(MD5) password expansion, length+payload AEAD records, per-record little-endian nonce, HKDF-SHA1 ss-subkey and optional Gost-derived outer framing. Selected AES key/salt lengths16/24/32; ChaCha32; tag16,nonce12. Standard mode removes only TLS-obfs envelope and reads/writes standard salt+AEAD frames. TLS-obfs retains prior handshake bytes/record handling and configured label. One common adapter implementation owns KDF/cipher selection, reused by old/new website/echo wrappers.

Relevant audited authoritative functions: Mihomo v1.19.21 commit3aa668c55704399ede35cd710a48c20111d23ea6 transport/shadowsocks/core/cipher.go PickCipher/Kdf; shadowaead/cipher.go KeySize/SaltSize/Encrypter/Decrypter and stream.go initReader/Writer (GPL3, existing license retained). No new Go protocol code enters product. Existing native Gost client framing attribution remains MIT Gost v2.11.5 obfs.go. Controlled counterpart extracts unchanged TLS-obfs types/methods from https://raw.githubusercontent.com/ginuerzh/gost/v2.11.5/obfs.go and uses locked tls-dissector v0.0.2-0.20220408131628-aac992c27451 plus go-log/log v0.2.0; complete original files, MIT license, zip hashes and fixture source are retained under the evidence directory. Downloaded sources are only development fixtures. Product adds three OpenSSL3 AES import symbols; existing OpenSSL DLLs and frozen AnyTLS/Mieru modules remain reusable.

Controlled evidence root:runs/coordination/standalone-kjx/backend/next/native-ss-compat. Exact commands/logs/hashes and SHA in HANDOFF.md there. Initial auto-budget CLI fixture produced35/40 responses after native automatic deadline adapted to14ms; unchanged product scheduler retained. Transport acceptance subsequently uses the already-supported explicit --node-timeout-ms6000 argument, not a changed product budget or relaxed pass predicate. ABI positives all passed independently. Final matrix includes binary passwords and retains the initial failure record.
