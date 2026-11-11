package com.keytokz.pcmonitor;

import android.content.Context;
import android.content.SharedPreferences;
import android.net.wifi.WifiManager;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Réseau : recherche du PC sur le Wi-Fi, test de connexion, réveil à distance. */
public final class Net {

    public static final int DISCOVERY_PORT = 8766;

    private Net() {
    }

    /** Un PC qui fait tourner PC Monitor. */
    public static final class Pc {
        public String name = "";
        public String host = "";
        public int port = 8765;
        public String macs = "";
    }

    // ------------------------------------------------------------ réglages
    public static SharedPreferences prefs(Context ctx) {
        return ctx.getSharedPreferences("pc", Context.MODE_PRIVATE);
    }

    public static Pc saved(Context ctx) {
        SharedPreferences p = prefs(ctx);
        String host = p.getString("host", "");
        if (host.isEmpty() || p.getString("code", "").isEmpty()) return null;
        Pc pc = new Pc();
        pc.host = host;
        pc.port = p.getInt("port", 8765);
        pc.name = p.getString("name", "PC");
        pc.macs = p.getString("macs", "");
        return pc;
    }

    public static String code(Context ctx) {
        return prefs(ctx).getString("code", "");
    }

    /** Ancienne adresse Tailscale (2.7), effacée au démarrage. */
    public static String remote(Context ctx) {
        return prefs(ctx).getString("remote", "");
    }

    /** Adresse à utiliser : celle du PC sur le Wi-Fi (l'accès à distance a été retiré). */
    public static String pickHost(Context ctx, Pc pc) {
        if (!remote(ctx).isEmpty()) prefs(ctx).edit().remove("remote").apply();
        return pc.host;
    }

    /** POST JSON simple (utilisé par le widget). Renvoie le code HTTP ou -1. */
    public static int post(String url, String json) {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(url).openConnection();
            c.setConnectTimeout(2500);
            c.setReadTimeout(5000);
            c.setRequestMethod("POST");
            c.setDoOutput(true);
            c.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            try (java.io.OutputStream o = c.getOutputStream()) {
                o.write(json.getBytes(StandardCharsets.UTF_8));
            }
            return c.getResponseCode();
        } catch (Exception e) {
            return -1;
        } finally {
            if (c != null) c.disconnect();
        }
    }

    public static void save(Context ctx, Pc pc, String code) {
        prefs(ctx).edit()
                .putString("host", pc.host)
                .putInt("port", pc.port)
                .putString("name", pc.name)
                .putString("macs", pc.macs)
                .putString("code", code)
                .apply();
    }

    // ------------------------------------------------------------ découverte
    /** Envoie « PCMONITOR? » en diffusion et collecte les réponses pendant timeoutMs. */
    public static List<Pc> discover(Context ctx, int timeoutMs) {
        Map<String, Pc> found = new LinkedHashMap<>();
        WifiManager.MulticastLock lock = null;
        try {
            WifiManager wm = (WifiManager) ctx.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wm != null) {
                lock = wm.createMulticastLock("pcmonitor");
                lock.setReferenceCounted(false);
                lock.acquire();
            }
        } catch (Exception ignored) {
        }
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setBroadcast(true);
            socket.setSoTimeout(300);
            byte[] msg = "PCMONITOR?".getBytes(StandardCharsets.UTF_8);
            long end = System.currentTimeMillis() + timeoutMs;
            long nextSend = 0;
            byte[] buf = new byte[4096];
            while (System.currentTimeMillis() < end) {
                if (System.currentTimeMillis() >= nextSend) {
                    for (InetAddress addr : broadcastAddresses()) {
                        try {
                            socket.send(new DatagramPacket(msg, msg.length, addr, DISCOVERY_PORT));
                        } catch (Exception ignored) {
                        }
                    }
                    nextSend = System.currentTimeMillis() + 800;
                }
                try {
                    DatagramPacket p = new DatagramPacket(buf, buf.length);
                    socket.receive(p);
                    JSONObject j = new JSONObject(new String(p.getData(), 0, p.getLength(), StandardCharsets.UTF_8));
                    if (!"pcmonitor".equals(j.optString("app"))) continue;
                    Pc pc = new Pc();
                    pc.name = j.optString("name", "PC");
                    pc.host = p.getAddress().getHostAddress();
                    pc.port = j.optInt("port", 8765);
                    pc.macs = joinMacs(j.optJSONArray("macs"));
                    found.put(pc.host, pc);
                } catch (SocketTimeoutException ignored) {
                } catch (Exception ignored) {
                }
            }
        } catch (Exception ignored) {
        } finally {
            if (lock != null) {
                try {
                    lock.release();
                } catch (Exception ignored) {
                }
            }
        }
        return new ArrayList<>(found.values());
    }

    static String joinMacs(JSONArray a) {
        if (a == null) return "";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < a.length(); i++) {
            String m = a.optString(i, "");
            if (m.isEmpty()) continue;
            if (sb.length() > 0) sb.append(',');
            sb.append(m);
        }
        return sb.toString();
    }

    static List<InetAddress> broadcastAddresses() {
        List<InetAddress> out = new ArrayList<>();
        try {
            for (NetworkInterface ni : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!ni.isUp() || ni.isLoopback()) continue;
                for (InterfaceAddress ia : ni.getInterfaceAddresses()) {
                    InetAddress b = ia.getBroadcast();
                    if (b != null && !out.contains(b)) out.add(b);
                }
            }
        } catch (Exception ignored) {
        }
        try {
            out.add(InetAddress.getByName("255.255.255.255"));
        } catch (Exception ignored) {
        }
        return out;
    }

    // ------------------------------------------------------------ HTTP
    /** GET simple : renvoie le code HTTP (ou -1 si le PC ne répond pas) et remplit body. */
    public static int get(String url, StringBuilder body) {
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(url).openConnection();
            c.setConnectTimeout(2500);
            c.setReadTimeout(3000);
            int code = c.getResponseCode();
            InputStream in = code >= 400 ? c.getErrorStream() : c.getInputStream();
            if (in != null && body != null) {
                ByteArrayOutputStream bos = new ByteArrayOutputStream();
                byte[] b = new byte[4096];
                int n;
                while ((n = in.read(b)) > 0) bos.write(b, 0, n);
                body.append(new String(bos.toByteArray(), StandardCharsets.UTF_8));
                in.close();
            }
            return code;
        } catch (Exception e) {
            return -1;
        } finally {
            if (c != null) c.disconnect();
        }
    }

    public static String base(Pc pc) {
        return "http://" + pc.host + ":" + pc.port;
    }

    // ------------------------------------------------------------ réveil (Wake-on-LAN)
    /** Envoie le « paquet magique » à chaque adresse MAC connue du PC. */
    public static boolean wake(String macsCsv) {
        if (macsCsv == null || macsCsv.isEmpty()) return false;
        boolean sent = false;
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setBroadcast(true);
            for (String mac : macsCsv.split(",")) {
                String hex = mac.replaceAll("[^0-9A-Fa-f]", "");
                if (hex.length() != 12) continue;
                byte[] m = new byte[6];
                for (int i = 0; i < 6; i++) m[i] = (byte) Integer.parseInt(hex.substring(i * 2, i * 2 + 2), 16);
                byte[] packet = new byte[6 + 16 * 6];
                for (int i = 0; i < 6; i++) packet[i] = (byte) 0xFF;
                for (int i = 6; i < packet.length; i += 6) System.arraycopy(m, 0, packet, i, 6);
                for (InetAddress addr : broadcastAddresses()) {
                    for (int port : new int[]{9, 7}) {
                        try {
                            socket.send(new DatagramPacket(packet, packet.length, addr, port));
                            sent = true;
                        } catch (Exception ignored) {
                        }
                    }
                }
            }
        } catch (Exception ignored) {
        }
        return sent;
    }
}
