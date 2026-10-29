package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/netip"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/fosrl/newt/logger"
	"github.com/fosrl/olm/dns"
	"golang.zx2c4.com/wireguard/tun"
	"golang.zx2c4.com/wireguard/tun/netstack"
)

const dialTimeout = 15 * time.Second

// forwardRule maps a local listen address to a host and port on the Pangolin network.
type forwardRule struct {
	listen string
	host   string
	port   uint16
}

// parseForwards parses LISTEN=TARGET values. LISTEN may be a bare port, which
// binds to 127.0.0.1.
func parseForwards(values []string) ([]forwardRule, error) {
	rules := make([]forwardRule, 0, len(values))
	for _, value := range values {
		listen, target, ok := strings.Cut(value, "=")
		listen, target = strings.TrimSpace(listen), strings.TrimSpace(target)
		if !ok || listen == "" || target == "" {
			return nil, fmt.Errorf("invalid forward %q: expected LISTEN=HOST:PORT", value)
		}
		if _, err := strconv.ParseUint(listen, 10, 16); err == nil {
			listen = net.JoinHostPort("127.0.0.1", listen)
		}
		if _, _, err := net.SplitHostPort(listen); err != nil {
			return nil, fmt.Errorf("invalid listen address in %q: %v", value, err)
		}
		host, portText, err := net.SplitHostPort(target)
		if err != nil {
			return nil, fmt.Errorf("invalid target in %q: %v", value, err)
		}
		port, err := strconv.ParseUint(portText, 10, 16)
		if err != nil || port == 0 {
			return nil, fmt.Errorf("invalid target port in %q", value)
		}
		rules = append(rules, forwardRule{listen: listen, host: host, port: uint16(port)})
	}
	return rules, nil
}

// userspaceTunnel creates the netstack device for olm and keeps the latest one,
// since olm builds a new device each time it reconnects.
type userspaceTunnel struct {
	current atomic.Pointer[tunnelStack]
}

type tunnelStack struct {
	net     *netstack.Net
	address netip.Addr
}

func (t *userspaceTunnel) createDevice(tunnelIP string, utilitySubnet string, mtu int) (tun.Device, error) {
	address, err := netip.ParseAddr(strings.Split(tunnelIP, "/")[0])
	if err != nil {
		return nil, fmt.Errorf("invalid tunnel IP %q: %w", tunnelIP, err)
	}

	// olm's DNS proxy answers resource aliases on the first address of the
	// utility subnet; packets to it are intercepted before they reach WireGuard.
	var dnsServers []netip.Addr
	if utilitySubnet != "" {
		if proxy, err := dns.PickIPFromSubnet(utilitySubnet); err == nil {
			dnsServers = append(dnsServers, proxy)
		} else {
			logger.Warn("No DNS proxy address for utility subnet %q: %v", utilitySubnet, err)
		}
	}

	device, stack, err := netstack.CreateNetTUN([]netip.Addr{address}, dnsServers, mtu)
	if err != nil {
		return nil, fmt.Errorf("creating userspace network stack: %w", err)
	}

	t.current.Store(&tunnelStack{net: stack, address: address})
	logger.Info("Userspace tunnel ready: address %s, DNS %v", address, dnsServers)
	return device, nil
}

func serveForward(ctx context.Context, listener net.Listener, rule forwardRule, tunnel *userspaceTunnel) {
	target := net.JoinHostPort(rule.host, strconv.Itoa(int(rule.port)))
	logger.Info("Forwarding %s to %s", listener.Addr(), target)
	for {
		conn, err := listener.Accept()
		if err != nil {
			if ctx.Err() != nil || errors.Is(err, net.ErrClosed) {
				return
			}
			logger.Warn("Accept on %s failed: %v", listener.Addr(), err)
			time.Sleep(100 * time.Millisecond)
			continue
		}
		go tunnel.forward(ctx, conn, rule, target)
	}
}

func (t *userspaceTunnel) forward(ctx context.Context, client net.Conn, rule forwardRule, target string) {
	defer client.Close()

	stack := t.current.Load()
	if stack == nil {
		logger.Warn("Connection to %s dropped: the tunnel is not connected yet", target)
		return
	}

	dialCtx, cancel := context.WithTimeout(ctx, dialTimeout)
	defer cancel()

	address, err := stack.resolve(dialCtx, rule.host)
	if err != nil {
		logger.Warn("Could not resolve %s through Pangolin DNS: %v", rule.host, err)
		return
	}

	remote, err := stack.net.DialContextTCPAddrPort(dialCtx, netip.AddrPortFrom(address, rule.port))
	if err != nil {
		logger.Warn("Could not connect to %s (%s) through the tunnel: %v", target, address, err)
		return
	}
	defer remote.Close()

	logger.Info("Forwarding connection from %s to %s (%s)", client.RemoteAddr(), target, address)
	pipe(client, remote)
}

func (s *tunnelStack) resolve(ctx context.Context, host string) (netip.Addr, error) {
	if address, err := netip.ParseAddr(host); err == nil {
		return address, nil
	}

	names, err := s.net.LookupContextHost(ctx, host)
	if err != nil {
		return netip.Addr{}, err
	}

	// The tunnel is IPv4, so prefer IPv4 answers.
	var fallback netip.Addr
	for _, name := range names {
		address, err := netip.ParseAddr(name)
		if err != nil {
			continue
		}
		if address.Is4() {
			return address, nil
		}
		if !fallback.IsValid() {
			fallback = address
		}
	}
	if fallback.IsValid() {
		return fallback, nil
	}
	return netip.Addr{}, fmt.Errorf("no usable address for %s", host)
}

type closeWriter interface {
	CloseWrite() error
}

// pipe copies in both directions, passing half-closes through so protocols
// that signal end-of-request with a FIN keep working.
func pipe(a, b net.Conn) {
	var wg sync.WaitGroup
	wg.Add(2)
	copyHalf := func(dst, src net.Conn) {
		defer wg.Done()
		_, _ = io.Copy(dst, src)
		if cw, ok := dst.(closeWriter); ok {
			_ = cw.CloseWrite()
		} else {
			_ = dst.Close()
		}
	}
	go copyHalf(a, b)
	go copyHalf(b, a)
	wg.Wait()
}
