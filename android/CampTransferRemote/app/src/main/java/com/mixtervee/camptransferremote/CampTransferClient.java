package com.mixtervee.camptransferremote;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
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

    static synchronized PairResult pairBest(String directHost, String relayHost, String code) throws Exception {
        String direct = normalizeHost(directHost);
        String relay = normalizeHost(relayHost);
        Exception firstError = null;

        if (preferRelay && !relay.isEmpty()) {
            try {
                PairResult result = pairAt(relay, RELAY_PORT, code, true);
                preferRelay = true;
                return result;
            } catch (Exception ex) {
                firstError = ex;
            }
            if (!direct.isEmpty()) {
                PairResult result = pairAt(direct, HTTP_PORT, code, false);
                preferRelay = false;
                return result;
            }
        } else {
            if (!direct.isEmpty()) {
                try {
                    PairResult result = pairAt(direct, HTTP_PORT, code, false);
                    preferRelay = false;
                    return result;
                } catch (Exception ex) {
                    firstError = ex;
                }
            }
            if (!relay.isEmpty()) {
                PairResult result = pairAt(relay, RELAY_PORT, code, true);
                preferRelay = true;
                return result;
            }
        }

        if (firstError != null) throw firstError;
        throw new IllegalArgumentException("No CampTransfer or relay address is configured");
    }

    static synchronized ControlResult sendBestControl(
            String directHost,
            String relayHost,
            String token,
            String action,
            Object value) throws Exception {
        String direct = normalizeHost(directHost);
        String relay = normalizeHost(relayHost);
        Exception firstError = null;

        if (preferRelay && !relay.isEmpty()) {
            try {
                ControlResult result = sendControlAt(relay, RELAY_PORT, token, action, value, true);
                preferRelay = true;
                return result;
            } catch (SecurityException ex) {
                throw ex;
            } catch (Exception ex) {
                firstError = ex;
            }
            if (!direct.isEmpty()) {
                ControlResult result = sendControlAt(direct, HTTP_PORT, token, action, value, false);
                preferRelay = false;
                return result;
            }
        } else {
            if (!direct.isEmpty()) {
                try {
                    ControlResult result = sendControlAt(direct, HTTP_PORT, token, action, value, false);
                    preferRelay = false;
                    return result;
                } catch (SecurityException ex) {
                    throw ex;
                } catch (Exception ex) {
                    firstError = ex;
                }
            }
            if (!relay.isEmpty()) {
                ControlResult result = sendControlAt(relay, RELAY_PORT, token, action, value, true);
                preferRelay = true;
                return result;
            }
        }

        if (firstError != null) throw firstError;
        throw new IllegalArgumentException("No CampTransfer or relay address is configured");
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
            return new JSONObject(readBody(connection, false));
        } finally {
            connection.disconnect();
        }
    }

    private static PairResult pairAt(String host, int port, String code, boolean viaRelay) throws Exception {
        JSONObject payload = new JSONObject();
        payload.put("code", code == null ? "" : code.trim());
        JSONObject response = postJson(host, port, "/api/pair", payload, null, 5000);
        String token = response.optString("token", "");
        if (token.isEmpty()) throw new IllegalStateException(response.optString("message", "Pairing failed"));
        return new PairResult(token, response.optString("message", "Paired"), viaRelay);
    }

    private static ControlResult sendControlAt(
            String host,
            int port,
            String token,
            String action,
            Object value,
            boolean viaRelay) throws Exception {
        JSONObject payload = new JSONObject();
        payload.put("action", action);
        if (value != null) payload.put("value", value);
        JSONObject response = postJson(host, port, "/api/control", payload, token, viaRelay ? 8000 : 4500);
        boolean ok = response.optBoolean("ok", false);
        String message = response.optString("message", ok ? "Command completed" : "Command failed");
        return new ControlResult(ok, message, viaRelay, response.optBoolean("acknowledged", !viaRelay));
    }

    private static JSONObject postJson(
            String host,
            int port,
            String path,
            JSONObject payload,
            String bearerToken,
            int timeoutMs) throws Exception {
        String normalized = normalizeHost(host);
        if (normalized.isEmpty()) throw new IllegalArgumentException("Server address is empty");

        URL url = new URL("http://" + normalized + ":" + port + path);
        HttpURLConnection connection = (HttpURLConnection) url.openConnection();
        connection.setRequestMethod("POST");
        connection.setConnectTimeout(timeoutMs);
        connection.setReadTimeout(timeoutMs);
        connection.setUseCaches(false);
        connection.setDoOutput(true);
        connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        connection.setRequestProperty("Connection", "close");
        if (bearerToken != null && !bearerToken.isEmpty())
            connection.setRequestProperty("Authorization", "Bearer " + bearerToken);

        byte[] body = payload.toString().getBytes(StandardCharsets.UTF_8);
        connection.setFixedLengthStreamingMode(body.length);
        try (OutputStream output = connection.getOutputStream()) {
            output.write(body);
        }

        try {
            int status = connection.getResponseCode();
            String text = readBody(connection, status >= 400);
            JSONObject response = text.isEmpty() ? new JSONObject() : new JSONObject(text);
            if (status == 401 || status == 403)
                throw new SecurityException(response.optString("message", "Pairing is required"));
            if (status < 200 || status >= 300)
                throw new IllegalStateException(response.optString("message", "Server returned HTTP " + status));
            return response;
        } finally {
            connection.disconnect();
        }
    }

    private static String readBody(HttpURLConnection connection, boolean error) throws Exception {
        InputStream stream = error ? connection.getErrorStream() : connection.getInputStream();
        if (stream == null) return "";
        StringBuilder text = new StringBuilder();
        try (BufferedReader reader = new BufferedReader(new InputStreamReader(stream, StandardCharsets.UTF_8))) {
            String line;
            while ((line = reader.readLine()) != null) text.append(line);
        }
        return text.toString();
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

    static final class PairResult {
        final String token;
        final String message;
        final boolean viaRelay;

        PairResult(String token, String message, boolean viaRelay) {
            this.token = token;
            this.message = message;
            this.viaRelay = viaRelay;
        }
    }

    static final class ControlResult {
        final boolean ok;
        final String message;
        final boolean viaRelay;
        final boolean acknowledged;

        ControlResult(boolean ok, String message, boolean viaRelay, boolean acknowledged) {
            this.ok = ok;
            this.message = message;
            this.viaRelay = viaRelay;
            this.acknowledged = acknowledged;
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
