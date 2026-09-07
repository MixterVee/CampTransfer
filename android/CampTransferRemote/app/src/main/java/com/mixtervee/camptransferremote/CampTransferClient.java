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
    private static final String DISCOVERY_MESSAGE = "CAMPTRANSFER_DISCOVER";

    private CampTransferClient() { }

    static JSONObject fetchStatus(String host) throws Exception {
        String normalized = normalizeHost(host);
        if (normalized.isEmpty()) throw new IllegalArgumentException("PC address is empty");

        URL url = new URL("http://" + normalized + ":" + HTTP_PORT + "/api/status");
        HttpURLConnection connection = (HttpURLConnection) url.openConnection();
        connection.setRequestMethod("GET");
        connection.setConnectTimeout(1800);
        connection.setReadTimeout(1800);
        connection.setUseCaches(false);
        connection.setRequestProperty("Connection", "close");

        try {
            int code = connection.getResponseCode();
            if (code != 200) throw new IllegalStateException("CampTransfer returned HTTP " + code);

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

    static final class DiscoveredPc {
        final String host;
        final String pcName;

        DiscoveredPc(String host, String pcName) {
            this.host = host;
            this.pcName = pcName;
        }
    }
}
