package agent

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"
)

func TestJSONManifestExceedsBinaryChunkLimit(t *testing.T) {
	// A 128 GiB disk already needs >4 MiB of chunk metadata. No disk or image
	// data is opened: this fixture exercises only the HTTP/JSON transport.
	value := strings.Repeat("x", 5*1024*1024)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"value":"`+value+`"}`)
	}))
	defer server.Close()
	c := &Client{base: server.URL, HTTP: server.Client()}
	var response struct{ Value string }
	if _, err := c.JSON(context.Background(), "GET", "/manifest", nil, &response); err != nil {
		t.Fatalf("valid large JSON manifest rejected: %v", err)
	}
	if response.Value != value {
		t.Fatal("JSON manifest changed during transfer")
	}
	// Even a JSON Content-Type cannot increase a binary request's bound.
	if _, _, err := c.Request(context.Background(), "GET", "/binary", nil, nil); err == nil {
		t.Fatal("oversized binary response accepted because of its Content-Type")
	}
}

func TestResponseLimitRejectsContentLengthBeforeReading(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		limit := maximumJSONResponseBytes
		if r.URL.Path == "/binary" {
			limit = ChunkSize + 65536
		}
		w.Header().Set("Content-Length", strconv.FormatInt(limit+1, 10))
		// Send no large allocation/body; the advertised length must be enough
		// to reject the response before decoding or waiting for missing data.
		_, _ = io.WriteString(w, "{}")
	}))
	defer server.Close()
	c := &Client{base: server.URL, HTTP: server.Client()}
	if _, err := c.JSON(context.Background(), "GET", "/manifest", nil, nil); err == nil || !strings.Contains(err.Error(), "excede limite") {
		t.Fatalf("JSON Content-Length bound not enforced first: %v", err)
	}
	if _, _, err := c.Request(context.Background(), "GET", "/binary", nil, nil); err == nil || !strings.Contains(err.Error(), "excede limite") {
		t.Fatalf("binary Content-Length bound not enforced first: %v", err)
	}
}

func TestChunkedResponseBoundCannotBeBypassed(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		w.(http.Flusher).Flush() // unknown Content-Length, chunked body
		_, _ = io.WriteString(w, strings.Repeat("x", 1025))
	}))
	defer server.Close()
	c := &Client{base: server.URL, HTTP: server.Client()}
	if _, _, err := c.requestLimited(context.Background(), "GET", "/", nil, nil, 1024); err == nil || !strings.Contains(err.Error(), "excede limite") {
		t.Fatalf("streamed response bypassed the bounded reader: %v", err)
	}
}
