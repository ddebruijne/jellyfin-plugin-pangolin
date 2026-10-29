// Command fakepangolin is a test harness for pangolin-userspace. It stands in
// for a Pangolin server and a single Newt site: it hands out a token, answers
// olm's registration over the websocket with one site, and runs that site as
// a userspace WireGuard peer with test services behind it:
//
//	10.77.0.5:389, 100.96.128.10:389   plain TCP, writes a banner
//	10.77.0.5:636, 100.96.128.10:636   TLS (self-signed for ldap.home.internal), writes a banner
//
// The alias ldap.home.internal resolves to 100.96.128.10 through olm's DNS proxy.
//
//	go run ./e2e/fakepangolin -listen :8080 -wg-endpoint <this host's IP>:51820
package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"math/big"
	"net"
	"net/http"
	"net/netip"
	"sync"
	"time"

	"github.com/gorilla/websocket"
	"golang.zx2c4.com/wireguard/conn"
	"golang.zx2c4.com/wireguard/device"
	"golang.zx2c4.com/wireguard/tun/netstack"
	"golang.zx2c4.com/wireguard/wgctrl/wgtypes"
)

const (
	serverIP   = "100.89.128.1"
	serverPort = 51820
	clientIP   = "100.90.128.2"
	resourceIP = "10.77.0.5"
	aliasIP    = "100.96.128.10"
	aliasName  = "ldap.home.internal"
)

func main() {
	listen := flag.String("listen", ":8080", "HTTP listen address for the fake Pangolin API")
	wgEndpoint := flag.String("wg-endpoint", "", "ip:port olm should use to reach the site (required)")
	flag.Parse()
	if *wgEndpoint == "" {
		log.Fatal("-wg-endpoint is required")
	}

	site, err := newSite()
	if err != nil {
		log.Fatal(err)
	}

	upgrader := websocket.Upgrader{CheckOrigin: func(*http.Request) bool { return true }}
	mux := http.NewServeMux()
	mux.HandleFunc("/api/v1", func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusOK) })
	mux.HandleFunc("/api/v1/auth/olm/get-token", func(w http.ResponseWriter, r *http.Request) {
		log.Printf("token requested")
		_ = json.NewEncoder(w).Encode(map[string]any{
			"success": true,
			"data":    map[string]any{"token": "test-token", "exitNodes": []any{}, "serverVersion": "1.24.0"},
		})
	})
	mux.HandleFunc("/api/v1/ws", func(w http.ResponseWriter, r *http.Request) {
		ws, err := upgrader.Upgrade(w, r, nil)
		if err != nil {
			log.Printf("websocket upgrade: %v", err)
			return
		}
		defer ws.Close()
		log.Printf("websocket connected")
		var once sync.Once
		for {
			var msg struct {
				Type string          `json:"type"`
				Data json.RawMessage `json:"data"`
			}
			if err := ws.ReadJSON(&msg); err != nil {
				log.Printf("websocket closed: %v", err)
				return
			}
			if msg.Type != "olm/wg/register" {
				continue
			}
			var reg struct {
				PublicKey string `json:"publicKey"`
			}
			_ = json.Unmarshal(msg.Data, &reg)
			once.Do(func() {
				if err := site.addClient(reg.PublicKey); err != nil {
					log.Printf("add client peer: %v", err)
					return
				}
				log.Printf("olm registered with key %s, sending site config", reg.PublicKey)
				_ = ws.WriteJSON(map[string]any{
					"type": "olm/wg/connect",
					"data": map[string]any{
						"tunnelIP":      clientIP + "/24",
						"utilitySubnet": "100.96.128.0/24",
						"sites": []any{map[string]any{
							"siteId":        1,
							"name":          "fake-site",
							"endpoint":      *wgEndpoint,
							"publicKey":     site.publicKey,
							"serverIP":      serverIP,
							"serverPort":    serverPort,
							"remoteSubnets": []string{"10.77.0.0/24"},
							"aliases":       []any{map[string]string{"alias": aliasName, "aliasAddress": aliasIP}},
						}},
					},
				})
			})
		}
	})

	log.Printf("fake Pangolin API on %s, site WireGuard on udp/%d", *listen, serverPort)
	log.Fatal(http.ListenAndServe(*listen, mux))
}

type site struct {
	dev       *device.Device
	publicKey string
}

func newSite() (*site, error) {
	key, err := wgtypes.GeneratePrivateKey()
	if err != nil {
		return nil, err
	}

	addrs := []netip.Addr{netip.MustParseAddr(serverIP), netip.MustParseAddr(resourceIP), netip.MustParseAddr(aliasIP)}
	tunDev, tnet, err := netstack.CreateNetTUN(addrs, nil, 1280)
	if err != nil {
		return nil, err
	}

	dev := device.NewDevice(tunDev, conn.NewDefaultBind(), device.NewLogger(device.LogLevelError, "site: "))
	if err := dev.IpcSet(fmt.Sprintf("private_key=%s\nlisten_port=%d\n", hex.EncodeToString(key[:]), serverPort)); err != nil {
		return nil, err
	}
	if err := dev.Up(); err != nil {
		return nil, err
	}

	cert, err := selfSignedCert(aliasName)
	if err != nil {
		return nil, err
	}

	for _, ip := range []string{resourceIP, aliasIP} {
		addr := netip.MustParseAddr(ip)
		plain, err := tnet.ListenTCPAddrPort(netip.AddrPortFrom(addr, 389))
		if err != nil {
			return nil, err
		}
		go serveBanner(plain, fmt.Sprintf("hello from %s:389 behind the tunnel\n", ip))

		secure, err := tnet.ListenTCPAddrPort(netip.AddrPortFrom(addr, 636))
		if err != nil {
			return nil, err
		}
		go serveBanner(tls.NewListener(secure, &tls.Config{Certificates: []tls.Certificate{cert}}), fmt.Sprintf("hello from %s:636 over TLS\n", ip))
	}

	// Answer olm's peer monitor the way Newt's wgtester does.
	probe, err := tnet.ListenUDPAddrPort(netip.AddrPortFrom(netip.MustParseAddr(serverIP), serverPort+1))
	if err != nil {
		return nil, err
	}
	go func() {
		buf := make([]byte, 2000)
		for {
			n, from, err := probe.ReadFrom(buf)
			if err != nil {
				return
			}
			if n < 13 || binary.BigEndian.Uint32(buf[0:4]) != 0xDEADBEEF || buf[4] != 1 {
				continue
			}
			reply := make([]byte, 13)
			binary.BigEndian.PutUint32(reply[0:4], 0xDEADBEEF)
			reply[4] = 2
			copy(reply[5:13], buf[5:13])
			_, _ = probe.WriteTo(reply, from)
		}
	}()

	return &site{dev: dev, publicKey: key.PublicKey().String()}, nil
}

func (s *site) addClient(publicKey string) error {
	raw, err := base64.StdEncoding.DecodeString(publicKey)
	if err != nil {
		return err
	}
	return s.dev.IpcSet(fmt.Sprintf("public_key=%s\nallowed_ip=%s/32\n", hex.EncodeToString(raw), clientIP))
}

func serveBanner(listener net.Listener, banner string) {
	for {
		c, err := listener.Accept()
		if err != nil {
			return
		}
		go func() {
			defer c.Close()
			log.Printf("connection on %s from %s", c.LocalAddr(), c.RemoteAddr())
			_, _ = c.Write([]byte(banner))
			_ = c.SetReadDeadline(time.Now().Add(2 * time.Second))
			_, _ = c.Read(make([]byte, 1))
		}()
	}
}

func selfSignedCert(name string) (tls.Certificate, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, err
	}
	template := &x509.Certificate{
		SerialNumber: big.NewInt(1),
		Subject:      pkix.Name{CommonName: name},
		DNSNames:     []string{name},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(24 * time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		return tls.Certificate{}, err
	}
	return tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key}, nil
}
