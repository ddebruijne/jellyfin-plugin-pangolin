// Command pangolin-userspace connects to a Pangolin network as a machine
// client without root privileges. Instead of a kernel TUN interface it runs
// WireGuard over a gVisor netstack inside the process, and forwards local TCP
// ports to private resources.
//
// It is launched and supervised by the Jellyfin Pangolin plugin, which passes
// the credentials through PANGOLIN_ENDPOINT, PANGOLIN_CLIENT_ID and
// PANGOLIN_CLIENT_SECRET.
package main

import (
	"context"
	"flag"
	"fmt"
	"net"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/fosrl/newt/logger"
	"github.com/fosrl/newt/network"
	olmpkg "github.com/fosrl/olm/olm"
)

var version = "dev"

type repeatedFlag []string

func (f *repeatedFlag) String() string { return strings.Join(*f, ",") }

func (f *repeatedFlag) Set(value string) error {
	*f = append(*f, value)
	return nil
}

func main() {
	socketPath := flag.String("socket", "", "path of the control API socket (required)")
	mtu := flag.Int("mtu", 1280, "tunnel MTU")
	logLevel := flag.String("log-level", "info", "log level (debug, info, warn, error)")
	disableRelay := flag.Bool("disable-relay", false, "only allow direct (hole-punched) connections to sites")
	upstreamDNS := flag.String("upstream-dns", "", "comma-separated DNS servers for names that are not Pangolin resources")
	showVersion := flag.Bool("version", false, "print the version and exit")
	var forwards repeatedFlag
	flag.Var(&forwards, "forward", "LISTEN=TARGET, e.g. 127.0.0.1:6360=ldap.home.internal:636 (repeatable)")
	flag.Parse()

	if *showVersion {
		fmt.Println(version)
		return
	}

	endpoint := os.Getenv("PANGOLIN_ENDPOINT")
	clientID := os.Getenv("PANGOLIN_CLIENT_ID")
	clientSecret := os.Getenv("PANGOLIN_CLIENT_SECRET")
	if endpoint == "" || clientID == "" || clientSecret == "" {
		fail("PANGOLIN_ENDPOINT, PANGOLIN_CLIENT_ID and PANGOLIN_CLIENT_SECRET must be set")
	}
	if *socketPath == "" {
		fail("--socket is required")
	}

	rules, err := parseForwards(forwards)
	if err != nil {
		fail(err.Error())
	}
	if len(rules) == 0 {
		fail("at least one --forward is required")
	}

	// Never touch the host's interfaces, routes or DNS. Set before olm starts,
	// because its system DNS monitor runs before the first connect.
	network.NativeConfigDisabled = true
	tunnel := &userspaceTunnel{}
	olmpkg.UserspaceDeviceFactory = tunnel.createDevice

	// Bind the local ports first so a port clash fails before connecting.
	listeners := make([]net.Listener, 0, len(rules))
	for _, rule := range rules {
		listener, err := net.Listen("tcp", rule.listen)
		if err != nil {
			fail(fmt.Sprintf("cannot listen on %s: %v", rule.listen, err))
		}
		listeners = append(listeners, listener)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	// A socket left behind by a crashed instance would make the API fail to start.
	_ = os.Remove(*socketPath)

	olm, err := olmpkg.Init(ctx, olmpkg.OlmConfig{
		LogLevel:   *logLevel,
		EnableAPI:  true,
		SocketPath: *socketPath,
		Version:    version,
		Agent:      "Jellyfin Pangolin plugin",
		OnTerminated: func() {
			logger.Info("Pangolin terminated the session")
			os.Exit(0)
		},
		OnAuthError: func(statusCode int, message string) {
			logger.Error("Authentication failed: %d %s", statusCode, message)
			os.Exit(1)
		},
		OnExit: func() {
			// olm has already closed the tunnel by the time this runs.
			logger.Info("Exit requested via API")
			os.Exit(0)
		},
	})
	if err != nil {
		fail(fmt.Sprintf("failed to initialise olm: %v", err))
	}

	if err := olm.StartApi(); err != nil {
		logger.Warn("Control API not available: %v", err)
	}

	for i, rule := range rules {
		go serveForward(ctx, listeners[i], rule, tunnel)
	}

	go olm.StartTunnel(olmpkg.TunnelConfig{
		Endpoint:             endpoint,
		ID:                   clientID,
		Secret:               clientSecret,
		MTU:                  *mtu,
		InterfaceName:        "pangolin",
		Holepunch:            true,
		PingIntervalDuration: 5 * time.Second,
		PingTimeoutDuration:  5 * time.Second,
		UpstreamDNS:          splitDNS(*upstreamDNS),
		DisableRelay:         *disableRelay,
	})

	<-ctx.Done()
	logger.Info("Received shutdown signal, stopping tunnel")
	for _, listener := range listeners {
		_ = listener.Close()
	}
	olm.Close()
}

func splitDNS(value string) []string {
	var servers []string
	for _, server := range strings.Split(value, ",") {
		server = strings.TrimSpace(server)
		if server == "" {
			continue
		}
		if _, _, err := net.SplitHostPort(server); err != nil {
			server = net.JoinHostPort(server, "53")
		}
		servers = append(servers, server)
	}
	return servers
}

func fail(message string) {
	fmt.Fprintf(os.Stderr, "ERROR: %s\n", message)
	os.Exit(2)
}
