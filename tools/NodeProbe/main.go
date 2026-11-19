package main

import (
	"context"
	"encoding/json"
	"strings"

	O "github.com/metacubex/mihomo/adapter/outbound"

	C "github.com/metacubex/mihomo/constant"
	"io"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"sync"
	"time"
)

// One-node development probe. Input is never logged or persisted.
func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	ctx, cancel := context.WithTimeout(ctx, 7*time.Second)
	defer cancel()
	// This one-shot worker exits at its single terminal boundary. The OS closes
	// sockets and reclaims adapter resources; no defer may extend its deadline.
	var terminal sync.Once
	finish := func(value any, code int) {
		terminal.Do(func() {
			if ctx.Err() != nil {
				status := "timeout"
				if ctx.Err() == context.Canceled {
					status = "cancelled"
				}
				value = map[string]string{"status": status}
				code = 1
			}
			json.NewEncoder(os.Stdout).Encode(value)
			os.Exit(code)
		})
	}
	go func() {
		<-ctx.Done()
		status := "timeout"
		if ctx.Err() == context.Canceled {
			status = "cancelled"
		}
		finish(map[string]string{"status": status}, 1)
	}()
	var cfg struct {
		ECHConfig         string   `json:"echConfig"`
		PortRange         string   `json:"portRange"`
		Type              string   `json:"type"`
		Server            string   `json:"server"`
		Port              int      `json:"port"`
		Password          string   `json:"password"`
		Username          string   `json:"username"`
		SNI               string   `json:"sni"`
		Transport         string   `json:"transport"`
		TargetURL         string   `json:"targetUrl"`
		TimeoutMs         int      `json:"timeoutMs"`
		Multiplexing      string   `json:"multiplexing"`
		HandshakeMode     string   `json:"handshakeMode"`
		TrafficPattern    string   `json:"trafficPattern"`
		ALPN              []string `json:"alpn"`
		ClientFingerprint string   `json:"clientFingerprint"`
		Fingerprint       string   `json:"fingerprint"`
	}
	input, inputErr := io.ReadAll(io.LimitReader(os.Stdin, 65537))
	if inputErr != nil || len(input) > 65536 {
		finish(map[string]string{"status": "invalid_input"}, 2)
	}
	decoder := json.NewDecoder(strings.NewReader(string(input)))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&cfg) != nil || cfg.Server == "" || (cfg.Port < 1 || cfg.Port > 65535) && !(cfg.Type == "mieru" && cfg.Port == 0 && cfg.PortRange != "") || cfg.Password == "" {
		finish(map[string]string{"status": "invalid_input"}, 2)
		os.Exit(2)
	}
	allowed := map[string]bool{"https://chatgpt.com/": true, "https://api.openai.com/v1/models": true, "https://www.google.com/": true, "https://www.youtube.com/": true, "https://api.anthropic.com/v1/models": true, "https://ipv4.icanhazip.com/": true, "https://api.ipify.org/": true}
	if !allowed[cfg.TargetURL] || cfg.TimeoutMs < 1 || cfg.TimeoutMs > 6000 {
		finish(map[string]string{"status": "invalid_input"}, 2)
	}
	var trailing any
	if decoder.Decode(&trailing) != io.EOF {
		finish(map[string]string{"status": "invalid_input"}, 2)
	}
	ctx, cancelRequest := context.WithTimeout(ctx, time.Duration(cfg.TimeoutMs)*time.Millisecond)
	defer cancelRequest()
	if cfg.Type == "anytls" && (cfg.PortRange != "" || cfg.Username != "" || cfg.Transport != "" || cfg.Multiplexing != "" || cfg.HandshakeMode != "" || cfg.TrafficPattern != "") || cfg.Type == "mieru" && (cfg.ECHConfig != "" || cfg.SNI != "" || len(cfg.ALPN) > 0 || cfg.ClientFingerprint != "" || cfg.Fingerprint != "") {
		finish(map[string]string{"status": "unsupported_configuration"}, 2)
	}
	var proxy C.ProxyAdapter
	var err error
	switch cfg.Type {
	case "anytls":
		proxy, err = O.NewAnyTLS(O.AnyTLSOption{Name: "probe", Server: cfg.Server, Port: cfg.Port, Password: cfg.Password, SNI: cfg.SNI, ALPN: cfg.ALPN, ClientFingerprint: cfg.ClientFingerprint, Fingerprint: cfg.Fingerprint, ECHOpts: O.ECHOptions{Enable: cfg.ECHConfig != "", Config: cfg.ECHConfig}})
	case "mieru":
		proxy, err = O.NewMieru(O.MieruOption{PortRange: cfg.PortRange, Name: "probe", Server: cfg.Server, Port: cfg.Port, Password: cfg.Password, UserName: cfg.Username, Transport: cfg.Transport, Multiplexing: cfg.Multiplexing, HandshakeMode: cfg.HandshakeMode, TrafficPattern: cfg.TrafficPattern})
	default:
		finish(map[string]string{"status": "unsupported_protocol"}, 2)
		os.Exit(2)
	}
	if err != nil {
		finish(map[string]string{"status": "configuration_rejected"}, 2)
		os.Exit(2)
	}
	// Adapter resources are owned by this one-shot process.
	transport := &http.Transport{Proxy: nil, DisableKeepAlives: true, DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
		host, port, err := net.SplitHostPort(address)
		if err != nil {
			return nil, err
		}
		p, err := strconv.ParseUint(port, 10, 16)
		if err != nil {
			return nil, err
		}
		return proxy.DialContext(ctx, &C.Metadata{Host: host, DstPort: uint16(p), NetWork: C.TCP})
	}}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	request, _ := http.NewRequestWithContext(ctx, http.MethodGet, cfg.TargetURL, nil)
	if cfg.TargetURL == "https://api.anthropic.com/v1/models" {
		request.Header.Set("anthropic-version", "2026-10-21")
	}
	start := time.Now()
	response, err := client.Do(request)
	result := map[string]any{"status": "ok", "elapsedMs": time.Since(start).Milliseconds()}
	if err != nil {
		result["status"] = "request_failed"
		if ctx.Err() == context.DeadlineExceeded {
			result["status"] = "timeout"
		} else if ctx.Err() == context.Canceled {
			result["status"] = "cancelled"
		}
	} else {
		result["httpStatus"] = response.StatusCode
		if cfg.TargetURL == "https://ipv4.icanhazip.com/" || cfg.TargetURL == "https://api.ipify.org/" {
			body, readErr := io.ReadAll(io.LimitReader(response.Body, 65))
			ip := net.ParseIP(strings.TrimSpace(string(body)))
			if readErr != nil || len(body) > 64 || response.StatusCode != 200 || ip == nil || ip.To4() == nil {
				result["status"] = "invalid_exit_response"
			} else {
				result["exitIp"] = ip.String()
			}
		}
		response.Body.Close()
	}
	code := 0
	if err != nil {
		code = 1
	}
	finish(result, code)
}
