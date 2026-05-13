// Quickstart Go container plugin — the smallest legal IP-1 plugin.
//
// Implements three endpoints:
//   GET  /health        - liveness
//   GET  /v1/metadata   - handler identity + API version
//   POST /v1/invoke     - the actual turn
//
// No SDK required. Standard library only.
package main

import (
	"encoding/json"
	"log"
	"net/http"
	"os"
	"strings"
)

const (
	apiVersion      = "0.24"
	handlerTypeName = "examples.echo.EchoPlugin"
)

type Message struct {
	Role    string `json:"role"`
	Content string `json:"content"`
}

type InvokeRequest struct {
	AgentID     string    `json:"agentId"`
	SessionID   string    `json:"sessionId"`
	Messages    []Message `json:"messages"`
	OpaqueState *string   `json:"opaqueState"`
}

type InvokeResponse struct {
	AssistantMessage string  `json:"assistantMessage"`
	OpaqueState      *string `json:"opaqueState"`
}

type Metadata struct {
	HandlerTypeName string `json:"handlerTypeName"`
	APIVersion      string `json:"apiVersion"`
}

func main() {
	http.HandleFunc("/health", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok"))
	})

	http.HandleFunc("/v1/metadata", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(Metadata{
			HandlerTypeName: handlerTypeName,
			APIVersion:      apiVersion,
		})
	})

	http.HandleFunc("/v1/invoke", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}

		var req InvokeRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			http.Error(w, "bad request", http.StatusBadRequest)
			return
		}

		// Find the latest user message and echo it back.
		var lastUser string
		for i := len(req.Messages) - 1; i >= 0; i-- {
			if req.Messages[i].Role == "user" {
				lastUser = strings.TrimSpace(req.Messages[i].Content)
				break
			}
		}

		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(InvokeResponse{
			AssistantMessage: "echo: " + lastUser,
		})
	})

	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}
	log.Printf("quickstart-go-plugin listening on :%s", port)
	log.Fatal(http.ListenAndServe(":"+port, nil))
}
