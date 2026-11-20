# Experimental C++ AnyTLS slice

Base: ab113a49046bfa0699517450606cb6810f4300dd. This is an isolated experiment, not an integrated portable release.

`anytls.cpp` adapts the authentication and single-stream frame semantics from Mihomo v1.19.21, commit `3aa668c55704399ede35cd710a48c20111d23ea6`: `listener/anytls/server.go`, `transport/anytls/session/frame.go`, `session.go`, `stream.go`, `util/string_map.go`. See the upstream [GPL-3.0 license](../../tools/NodeProbe/LICENSE.mihomo), also included in the evidence packages. Existing `shadowsocks.cpp` supplies numeric-endpoint dialing, deadline/cancellation and BIO control code.

The slice opens one outbound connection and one stream to the supplied HTTPS target. It sends SHA256 authentication, settings, SYN, SOCKS destination and PSH frames; reads PSH payload while discarding bounded control payloads. It has no listener, host-controller request, selection, routing or subscription logic. Outer and inner TLS both verify the provided CA and hostname. Static ECH requires TLS 1.3 and `SSL_ech_get1_status == 1` before sending authentication. Rejected ECH never falls back to plaintext SNI authentication.

OpenSSL 4.0.0 official source archive: SHA256 `c32cf49a959c4f345f9606982dd36e7d28f7c58b19c2e25d75624d2b3d2f79ac`, verified against official release metadata. API: <https://github.com/openssl/openssl/blob/openssl-4.0.0/doc/man3/SSL_set1_echstore.pod>. Apache-2.0 license retained. OpenSSL 3.5.6 remains a separate plain-TLS build; it has no ECH API and rejects a nonempty ECH configuration.

`Build-AnyTls.ps1` builds a standalone DLL with the existing Cdecl initialization/shutdown ABI, a plain `ProbeWebsite` wrapper and explicit `ProbeAnyTlsWebsite` ECH argument/status. New code is necessary for the two TLS layers and AnyTLS frame boundary; no product frontend, scheduler or Go worker was changed.

Evidence and synthetic server/caller: `runs/coordination/standalone-kjx/backend/next/cpp-anytls` under the coordination root. Synthetic test server uses the locked actual Mihomo listener/authentication/session implementation, never dials remote destinations, and is outside the product Job measurement. No real KJX configuration or credentials were read. All real-node results, product UI integration, full-product resource gate and independent exact-commit acceptance remain unverified.
