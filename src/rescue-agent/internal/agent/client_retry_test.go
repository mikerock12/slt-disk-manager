package agent

import (
	"context"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"
)

func TestFetchChunkRetriesDroppedConnectionAndServerErrors(t *testing.T) {
	saved := fetchRetryDelays
	fetchRetryDelays = []time.Duration{time.Millisecond, time.Millisecond, time.Millisecond}
	defer func() { fetchRetryDelays = saved }()
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch calls.Add(1) {
		case 1:
			// Connection reset in the middle of a chunk, as seen on a flaky LAN.
			conn, _, _ := w.(http.Hijacker).Hijack()
			_ = conn.Close()
		case 2:
			http.Error(w, "busy", http.StatusServiceUnavailable)
		default:
			_, _ = w.Write([]byte("chunk"))
		}
	}))
	defer server.Close()
	c := &Client{base: server.URL, HTTP: server.Client()}
	data, err := c.FetchChunk(context.Background(), "/agent/v1/jobs/x/media/chunks/0")
	if err != nil || string(data) != "chunk" || calls.Load() != 3 {
		t.Fatalf("transient failures not retried: data=%q err=%v calls=%d", data, err, calls.Load())
	}
}

func TestFetchChunkDoesNotRetryRejections(t *testing.T) {
	saved := fetchRetryDelays
	fetchRetryDelays = []time.Duration{time.Millisecond, time.Millisecond}
	defer func() { fetchRetryDelays = saved }()
	var calls atomic.Int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		http.Error(w, "job cancelled", http.StatusConflict)
	}))
	defer server.Close()
	c := &Client{base: server.URL, HTTP: server.Client()}
	if _, err := c.FetchChunk(context.Background(), "/chunk"); err == nil || calls.Load() != 1 {
		t.Fatalf("rejection must fail at once: err=%v calls=%d", err, calls.Load())
	}
	// Persistent outages stop after the bounded attempts.
	calls.Store(0)
	down := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		http.Error(w, "down", http.StatusBadGateway)
	}))
	defer down.Close()
	c = &Client{base: down.URL, HTTP: down.Client()}
	if _, err := c.FetchChunk(context.Background(), "/chunk"); err == nil || calls.Load() != 3 {
		t.Fatalf("bounded retries expected: err=%v calls=%d", err, calls.Load())
	}
}
