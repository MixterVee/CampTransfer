package com.mixtervee.camptransferremote;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.URL;
import java.nio.charset.StandardCharsets;

final class CampTransferClient {
    static final int HTTP_PORT = 45827;
    static final int DISCOVERY_PORT = 45828;
    static final int RELAY_PORT = 80;
    private static final String DISCOVERY_MESSAGE = "CAMPTRANSFER_DISCOVER";
    private static final long DIRECT_REPROBE_MS = 30_000L;

    private static boolean preferRelay;
    private static long lastDirectProbeMs;

    private CampTransferClient() { }

    static JSONObject fetchStatus(String host) throws Exception {
        return fetchStatusAt(host, HTTP_PORT, 1600);
    }

    static JSONObject fetchRelayStatus(String host) throws Exception {
        return fetchStatusAt(host, RELAY_PORT, 1800);
    }

    static synchronized StatusResult fetchBestStatus(String directHost, String relayHost) throws Exception {
        String direct = normalizeHost(directHost);
        String relay = normalizeHost(relayHost);
        if (direct.isEmpty() && relay.isEmpty())
            throw new IllegalArgumentException("No CampTransfer or relay address is configured");

        Exception directError = null;
        Exception relayError = null;
        long now = System.currentTimeMillis();

        if (preferRelay && !relay.isEmpty()) {
            boolean shouldProbeDirect = !direct.isEmpty() && now - lastDirectProbeMs >= DIRECT_REPROBE_MS;
            if (shouldProbeDirect) {
                lastDirectProbeMs = now;
                try {
                    JSONObject status = fetchStatusAt(direct, HTTP_PORT, 1000);
                    preferRelay = false;
                    return new StatusResult(status, false, direct);
                } catch (Exception ex) {
                    directError = ex;
                }
            }

            try {
                JSONObject status = fetchStatusAt(relay, RELAY_PORT, 1800);
                preferRelay = true;
                return new StatusResult(status, true, relay);
            } catch (Exception ex) {
                relayError = ex;
            }

            if (!direct.isEmpty() && !shouldProbeDirect) {
                try {
                    JSONObject status = fetchStatusAt(direct, HTTP_PORT, 1200);
                    preferRelay = false;
                    return new StatusResult(status, false, direct);
                } catch (Exception ex) {
                    directError = ex;
                }
            }
        } else {
            if (!direct.isEmpty()) {
                lastDirectProbeMs = now;
                try {
                    JSONObject status = fetchStatusAt(direct, HTTP_PORT, 1200);
                    preferRelay = false;
                    return new StatusResult(status, false, direct);
                } catch (Exception ex) {
                    directError = ex;
                }
            }

            if (!relay.isEmpty()) {
                try {
                    JSONObject status = fetchStatusAt(relay, RELAY_PORT, 1800);
                    preferRelay = true;
                    return new StatusResult(status, true, relay);
                } catch (Exception ex) {
                    relayError = ex;
                }
            }
        }

        String message = "Could not reach CampTransfer";
        if (directError != null && relayError != null)
            message += " directly or through the relay";
        else if (relayError != null)
            message += " through the relay";
        Exception cause = relayError != null ? relayError : directError;
        throw new IllegalStateException(message, cause);
    }

    private static JSONObject fetchStatusAt(String host, int port, int timeoutMs) throws Exception {
        String normalized = normalizeHost(host);
        if (normalized.isEmpty()) throw new IllegalArgumentException("Server address is empty");

        URL url = new URL("http://" + normalized + ":" + port + "/api/status");
        HttpURLConnection connection = (HttpURLConnection) url.openConnection();
        connection.setRequestMethod("GET");
        connection.setConnectTimeout(timeoutMs);
        connection.setReadTimeout(timeoutMs);
        connection.setUseCaches(false);
        connection.setRequestProperty("Connection", "close");

        try {
            int code = connection.getResponseCode();
            if (code != 200) throw new IllegalStateException("Server returned HTTP " + code);

            StringBuilder text = new StringBuilder();
            try (BufferedReader reader = new BufferedReader(
                    new InputStreamReader(connection.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while ((line = reader.readLine()) != null) text.append(line);
            }
            return new JSONObject(text.toString());
        } finally {
            connection.disconnect();
        }
    }

    static DiscoveredPc discover() throws Exception {
        byte[] request = DISCOVERY_MESSAGE.getBytes(StandardCharsets.UTF_8);
        try (DatagramSocket socket = new DatagramSocket()) {
            socket.setBroadcast(true);
            socket.setSoTimeout(1800);

            DatagramPacket packet = new DatagramPacket(
                    request,
                    request.length,
                    InetAddress.getByName("255.255.255.255"),
                    DISCOVERY_PORT);
            socket.send(packet);

            byte[] buffer = new byte[2048];
            DatagramPacket response = new DatagramPacket(buffer, buffer.length);
            socket.receive(response);

            String json = new String(response.getData(), 0, response.getLength(), StandardCharsets.UTF_8);
            JSONObject payload = new JSONObject(json);
            if (!"CampTransfer".equals(payload.optString("service")))
                throw new IllegalStateException("Unexpected discovery response");

            String host = response.getAddress().getHostAddress();
            String pcName = payload.optString("pcName", host);
            return new DiscoveredPc(host, pcName);
        }
    }

    static String normalizeHost(String value) {
        if (value == null) return "";
        String host = value.trim();
        if (host.startsWith("http://")) host = host.substring(7);
        if (host.startsWith("https://")) host = host.substring(8);
        int slash = host.indexOf('/');
        if (slash >= 0) host = host.substring(0, slash);
        int portMarker = host.lastIndexOf(':');
        if (portMarker > 0 && host.substring(portMarker + 1).matches("\\d+"))
            host = host.substring(0, portMarker);
        return host.trim();
    }

    static final class StatusResult {
        final JSONObject status;
        final boolean viaRelay;
        final String sourceHost;

        StatusResult(JSONObject status, boolean viaRelay, String sourceHost) {
            this.status = status;
            this.viaRelay = viaRelay;
            this.sourceHost = sourceHost;
        }
    }

    static final class DiscoveredPc {
        final String host;
        final String pcName;

        DiscoveredPc(String host, String pcName) {
            this.host = host;
            this.pcName = pcName;
        }
    }
}
