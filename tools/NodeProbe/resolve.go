package main

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"time"

	"github.com/miekg/dns"
)

func resolveEntry(parent context.Context) int {
	var input struct {
		Host      string   `json:"host"`
		Endpoints []string `json:"endpoints"`
	}
	decoder := json.NewDecoder(io.LimitReader(os.Stdin, 8192))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&input) != nil || len(input.Endpoints) < 1 || len(input.Endpoints) > 8 {
		return 2
	}
	var extra any
	if decoder.Decode(&extra) != io.EOF {
		return 2
	}
	name := dns.Fqdn(input.Host)
	if _, valid := dns.IsDomainName(name); !valid || name == "." {
		return 2
	}
	for _, endpoint := range input.Endpoints {
		u, err := url.Parse(endpoint)
		if err != nil || u.Scheme != "https" || u.Hostname() == "" || u.User != nil || u.RawQuery != "" || u.Fragment != "" {
			return 2
		}
	}
	ctx, cancel := context.WithTimeout(parent, 10*time.Second)
	defer cancel()
	transport := &http.Transport{}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport, Timeout: 3 * time.Second,
		CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	for _, kind := range []uint16{dns.TypeA, dns.TypeAAAA} {
		query := new(dns.Msg).SetQuestion(name, kind)
		body, err := query.Pack()
		if err != nil {
			return 2
		}
		for _, endpoint := range input.Endpoints {
			if address := lookup(ctx, client, endpoint, query, body); address != nil {
				if json.NewEncoder(os.Stdout).Encode(map[string]string{"address": address.String()}) != nil {
					return 1
				}
				return 0
			}
		}
	}
	return 1
}

func lookup(ctx context.Context, client *http.Client, endpoint string, query *dns.Msg, body []byte) net.IP {
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, bytes.NewReader(body))
	if err != nil {
		return nil
	}
	request.Header.Set("Content-Type", "application/dns-message")
	request.Header.Set("Accept", "application/dns-message")
	reply, err := client.Do(request)
	if err != nil {
		return nil
	}
	defer reply.Body.Close()
	if reply.StatusCode != http.StatusOK {
		return nil
	}
	data, err := io.ReadAll(io.LimitReader(reply.Body, 65536))
	var message dns.Msg
	if err != nil || len(data) > 65535 || message.Unpack(data) != nil || !message.Response || message.Truncated ||
		message.Id != query.Id || message.Rcode != dns.RcodeSuccess || len(message.Question) != 1 ||
		!strings.EqualFold(message.Question[0].Name, query.Question[0].Name) ||
		message.Question[0].Qtype != query.Question[0].Qtype || message.Question[0].Qclass != dns.ClassINET {
		return nil
	}
	name := query.Question[0].Name
	for range len(message.Answer) {
		var next string
		for _, answer := range message.Answer {
			if !strings.EqualFold(answer.Header().Name, name) || answer.Header().Class != dns.ClassINET {
				continue
			}
			if alias, ok := answer.(*dns.CNAME); ok {
				next = alias.Target
			}
			if answer.Header().Rrtype != query.Question[0].Qtype {
				continue
			}
			var address net.IP
			switch ip := answer.(type) {
			case *dns.A:
				address = ip.A
			case *dns.AAAA:
				address = ip.AAAA
			}
			if address != nil && address.IsGlobalUnicast() && !address.IsLoopback() {
				return address
			}
		}
		if next == "" {
			break
		}
		name = next
	}
	return nil
}
