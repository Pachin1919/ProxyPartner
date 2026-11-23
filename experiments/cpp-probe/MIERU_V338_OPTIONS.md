# Mieru v3.38.0 isolated probe options

Upstream: official local `github.com/enfein/mieru/v3@v3.38.0`, commit `b961978c3be9dd26b94158487c760858e19d1db2`, GPL-3.0-or-later. Adapted functions: cipher/cipher.go newNonce, addUserHintToNonce; common/ascii.go ToCommon64Set; protocol/metadata.go and underlay_stream.go; trafficpattern/config.go. Existing v3.29 wire base and Monocypher4.0.3 BSD-2-Clause implementation remain as recorded in MIERU_PROVENANCE.md. This is not an MIT-only release.

## ABI

Old ProbeMieruWebsite remains unchanged, without client shaping. Configurations containing the known pattern MUST use the new symbol:

```c
int ProbeMieruWebsiteV2(
    const char* address, const char* port,
    const char* username, const char* password,
    const char* trafficPattern, const char* multiplexing,
    const char* handshakeMode,
    const char* host, const char* path, const char* headers,
    unsigned remainingMilliseconds, void* stopEvent, int* httpStatus);
```

All strings are non-null NUL-terminated UTF-8; absent optional values use empty strings. Initialize/shutdown symbols and verified CA behavior are unchanged. Return1 means an observed HTTP response, return0/status0 means failure. Unknown patterns/multiplexing or explicit handshake modes return0 before dialing.

Supported actual pattern: `CKPfirUDEAAaBAgAEAAiCAgCEAEYCSAJKgUIABD/ATICCAA=`. Official decoding gives seed916631459, unlockAll=false, TCP fragmentation=false/maxSleep0, nonce PRINTABLE_SUBSET/min=max9, middle padding max0, end padding max255, low entropy OFF. Rotation generated for the disabled low-entropy mode has no wire effect. TCP does not use applyToAllUDPPacket. Native sends the exact Common64Set prefix length9, v3.38 username hint (SHA256(username||nonce[0:16]), first4 bytes into nonce[20:24]), no middle padding, bounded printable suffix0..255, and ordinary encrypted protocol6 data. It does not enable fragmentation or low-entropy encoding. Random suffix length uses native cryptographic randomness, not the reference implementation's host-dependent biased PRNG/distribution; no identical statistical fingerprint claim is made.

HIGH and LOW map to local upstream mux reuse factors3 and1 in appctlcommon/client.go; mux.go uses these to choose existing underlays. Neither is a handshake field. This ABI owns one fresh TCP connection/session per call, so its active-underlay set is always empty: both values take the official cold-dial case. There is no cross-call pooling implementation. This is explicit independent/cold probing, not full proxy-client scheduling equivalence. The parent CLI should preserve that measurement method/provenance and must not mix warm Go and cold native timing histories silently. Absent handshake-mode uses the existing wait-for-SOCKS-response behavior. Other explicit modes are rejected.

## OpenSSL and lifecycle

Build-Mieru.ps1 adds `-OpenSslMajor 4`; default3 preserves existing callers. Pass matching OpenSslBin. The4 build imports libcrypto-4-x64/libssl-4-x64, enabling sharing of the exact libraries used by AnyTLS. It still uses unmodified Monocypher for XChaCha20-Poly1305. All request keys, nonces, buffers, sequence counters, sockets and SSL objects belong to one stack-local channel. Shared SSL_CTX/BIO_METHOD are initialized once and then read only. Initialize and shutdown MUST be serialized by the embedding CLI; shutdown/unload MUST occur after all calls finish. No concurrent reinitialization/unload is supported. Concurrent-probe safety is a source-review conclusion; a same-process multithreaded stress verdict is NOT_RUN here. Full-process40MiB is the parent CLI's gate, not established by managed test-caller memory.

## Evidence

Coordination root: runs/coordination/standalone-kjx/backend/next/cpp-mieru-options. Official decoder output in decoded-options.log. Four final runs use scalar/range ports × HIGH/LOW, with17 cases each:8 authenticated HTTPS204 responses covering five fixed website hostnames/paths; wrong user/password/target; expired deadline/signaled cancellation; invalid range; unknown pattern/mux/handshake. The fixture checks wire nonce shape and username hints without logging nonce bytes or credentials. Both range endpoints were exercised. Official server is v3.38.0 via a pinned local go.mod replacement; no upstream source modification. These are controlled synthetic routes, not real-node acceptance.

The output is package-openssl4/ProxyPartner.NativeProbe.dll plus its two OpenSSL4 DLLs. Parent may rename the adapter DLL while loading symbols by module handle. Source diff and hashes, repeated commands and run logs are provided in the evidence receipt. No host config, third-party encrypted file or KJX document was accessed. No subagent, push, history cleanup or publication.
