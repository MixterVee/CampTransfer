package com.mixtervee.camptransferremote;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.Locale;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

public class MainActivity extends Activity {
    static final String PREFS = "camptransfer_remote";
    static final String PREF_HOST = "host";

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private SharedPreferences prefs;
    private ScheduledExecutorService poller;

    private EditText hostEdit;
    private TextView connectionText;
    private TextView activeFileText;
    private TextView progressText;
    private ProgressBar progressBar;
    private TextView statsText;
    private TextView destinationText;
    private TextView queueText;
    private TextView finishActionText;
    private Button discoverButton;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        prefs = getSharedPreferences(PREFS, MODE_PRIVATE);
        buildUi();
        requestNotificationPermissionIfNeeded();

        String savedHost = prefs.getString(PREF_HOST, "");
        hostEdit.setText(savedHost);
        if (savedHost == null || savedHost.trim().isEmpty()) {
            discoverPc();
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        startPolling();
    }

    @Override
    protected void onPause() {
        stopPolling();
        super.onPause();
    }

    private void buildUi() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(16), dp(16), dp(16), dp(24));
        scroll.addView(root, new ScrollView.LayoutParams(
                ScrollView.LayoutParams.MATCH_PARENT,
                ScrollView.LayoutParams.WRAP_CONTENT));

        TextView title = text("CampTransfer Remote", 26, true);
        root.addView(title);

        connectionText = text("Not connected", 15, false);
        connectionText.setPadding(0, dp(4), 0, dp(12));
        root.addView(connectionText);

        LinearLayout hostRow = new LinearLayout(this);
        hostRow.setOrientation(LinearLayout.HORIZONTAL);
        hostRow.setGravity(Gravity.CENTER_VERTICAL);

        hostEdit = new EditText(this);
        hostEdit.setSingleLine(true);
        hostEdit.setHint("PC address or hostname");
        hostRow.addView(hostEdit, new LinearLayout.LayoutParams(0, dp(48), 1f));

        Button connectButton = new Button(this);
        connectButton.setText("Connect");
        connectButton.setOnClickListener(v -> connectToEnteredHost());
        hostRow.addView(connectButton, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, dp(48)));
        root.addView(hostRow);

        discoverButton = new Button(this);
        discoverButton.setText("Discover CampTransfer on Wi-Fi");
        discoverButton.setOnClickListener(v -> discoverPc());
        LinearLayout.LayoutParams discoverParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        discoverParams.topMargin = dp(8);
        root.addView(discoverButton, discoverParams);

        View divider1 = divider();
        LinearLayout.LayoutParams divParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(1));
        divParams.topMargin = dp(18);
        divParams.bottomMargin = dp(18);
        root.addView(divider1, divParams);

        TextView nowTitle = text("NOW TRANSFERRING", 13, true);
        root.addView(nowTitle);

        activeFileText = text("No active transfer", 21, true);
        activeFileText.setPadding(0, dp(8), 0, dp(10));
        root.addView(activeFileText);

        progressBar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progressBar.setMax(1000);
        progressBar.setProgress(0);
        root.addView(progressBar, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(12)));

        progressText = text("0.0%", 17, true);
        progressText.setPadding(0, dp(8), 0, 0);
        root.addView(progressText);

        statsText = text("Speed —    ETA —", 17, false);
        statsText.setPadding(0, dp(6), 0, 0);
        root.addView(statsText);

        destinationText = text("", 14, false);
        destinationText.setPadding(0, dp(6), 0, 0);
        root.addView(destinationText);

        finishActionText = text("", 14, false);
        finishActionText.setPadding(0, dp(4), 0, 0);
        root.addView(finishActionText);

        View divider2 = divider();
        LinearLayout.LayoutParams div2Params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(1));
        div2Params.topMargin = dp(18);
        div2Params.bottomMargin = dp(18);
        root.addView(divider2, div2Params);

        TextView queueTitle = text("QUEUE", 13, true);
        root.addView(queueTitle);

        queueText = text("No queued files", 15, false);
        queueText.setPadding(0, dp(8), 0, 0);
        queueText.setLineSpacing(0f, 1.15f);
        root.addView(queueText);

        setContentView(scroll);
    }

    private void connectToEnteredHost() {
        String host = CampTransferClient.normalizeHost(hostEdit.getText().toString());
        if (host.isEmpty()) {
            connectionText.setText("Enter the laptop IP address or hostname first.");
            return;
        }
        hostEdit.setText(host);
        prefs.edit().putString(PREF_HOST, host).apply();
        connectionText.setText("Connecting to " + host + "…");
        pollOnce();
    }

    private void discoverPc() {
        discoverButton.setEnabled(false);
        connectionText.setText("Looking for CampTransfer on this network…");
        new Thread(() -> {
            try {
                CampTransferClient.DiscoveredPc pc = CampTransferClient.discover();
                prefs.edit().putString(PREF_HOST, pc.host).apply();
                mainHandler.post(() -> {
                    hostEdit.setText(pc.host);
                    connectionText.setText("Found " + pc.pcName + " — connecting…");
                    discoverButton.setEnabled(true);
                    pollOnce();
                });
            } catch (Exception ex) {
                mainHandler.post(() -> {
                    discoverButton.setEnabled(true);
                    String saved = prefs.getString(PREF_HOST, "");
                    if (saved != null && !saved.isEmpty()) {
                        connectionText.setText("Discovery unavailable — trying saved PC " + saved);
                        pollOnce();
                    } else {
                        connectionText.setText("CampTransfer not found. Enter the laptop IP address if needed.");
                    }
                });
            }
        }, "CampTransfer-discovery").start();
    }

    private void startPolling() {
        stopPolling();
        poller = Executors.newSingleThreadScheduledExecutor();
        poller.scheduleWithFixedDelay(this::pollInBackground, 0, 1, TimeUnit.SECONDS);
    }

    private void stopPolling() {
        if (poller != null) {
            poller.shutdownNow();
            poller = null;
        }
    }

    private void pollOnce() {
        new Thread(this::pollInBackground, "CampTransfer-poll-once").start();
    }

    private void pollInBackground() {
        String host = prefs.getString(PREF_HOST, "");
        if (host == null || host.trim().isEmpty()) return;

        try {
            JSONObject status = CampTransferClient.fetchStatus(host);
            mainHandler.post(() -> applyStatus(host, status));
        } catch (Exception ex) {
            mainHandler.post(() -> connectionText.setText(
                    "Not connected to " + host + " — " + friendlyError(ex)));
        }
    }

    private void applyStatus(String host, JSONObject status) {
        String pcName = status.optString("pcName", host);
        String state = status.optString("state", "Ready");
        int filesLeft = status.optInt("filesLeft", 0);
        connectionText.setText("Connected to " + pcName + " • " + state);

        String whenFinished = status.optString("whenFinished", "Do nothing");
        finishActionText.setText("When finished: " + whenFinished);

        JSONObject active = status.optJSONObject("active");
        String activeId = active == null ? "" : active.optString("id", "");
        if (active == null) {
            activeFileText.setText(filesLeft == 0 ? "Queue complete / idle" : "Waiting for next file");
            progressBar.setProgress(filesLeft == 0 ? 1000 : 0);
            progressText.setText(filesLeft == 0 ? "100%" : "0.0%");
            statsText.setText("Status: " + state);
            destinationText.setText("");
        } else {
            String name = active.optString("fileName", "Transfer");
            String operation = active.optString("operation", "Copy");
            double percent = active.optDouble("progressPercent", 0d);
            String speed = active.optString("speed", "");
            String eta = active.optString("eta", "");
            String itemStatus = active.optString("status", "Transferring");
            String destination = active.optString("destination", "");

            activeFileText.setText(name);
            progressBar.setProgress((int) Math.max(0, Math.min(1000, Math.round(percent * 10))));
            progressText.setText(String.format(Locale.getDefault(), "%.1f%% • %s • %s", percent, operation, itemStatus));
            statsText.setText("Speed " + blankAsDash(speed) + "    ETA " + blankAsDash(eta));
            destinationText.setText(destination.isEmpty() ? "" : "To: " + destination);
        }

        JSONArray queue = status.optJSONArray("queue");
        if (queue == null || queue.length() == 0) {
            queueText.setText("No queued files");
        } else {
            StringBuilder lines = new StringBuilder();
            for (int i = 0; i < queue.length(); i++) {
                JSONObject item = queue.optJSONObject(i);
                if (item == null) continue;
                if (lines.length() > 0) lines.append("\n\n");
                boolean complete = item.optBoolean("completed", false);
                boolean isActive = !activeId.isEmpty() && activeId.equals(item.optString("id", ""));
                String marker = complete ? "✓" : isActive ? "▶" : "•";
                lines.append(marker).append(' ')
                        .append(item.optString("operation", "Copy"))
                        .append("  ")
                        .append(item.optString("fileName", "File"))
                        .append("\n   ")
                        .append(String.format(Locale.getDefault(), "%.1f%%", item.optDouble("progressPercent", 0d)))
                        .append(" • ")
                        .append(item.optString("status", "Queued"));
            }
            queueText.setText(lines.toString());
        }

        if (filesLeft > 0) startBackgroundMonitor();
    }

    private void startBackgroundMonitor() {
        Intent intent = new Intent(this, MonitorService.class);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
            startForegroundService(intent);
        else
            startService(intent);
    }

    private void requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= 33 &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 1001);
        }
    }

    private TextView text(String value, int sp, boolean bold) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(sp);
        if (bold) view.setTypeface(view.getTypeface(), android.graphics.Typeface.BOLD);
        return view;
    }

    private View divider() {
        View view = new View(this);
        view.setBackgroundColor(0x44808080);
        return view;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private static String blankAsDash(String value) {
        return value == null || value.trim().isEmpty() ? "—" : value;
    }

    private static String friendlyError(Exception ex) {
        String text = ex.getMessage();
        if (text == null || text.trim().isEmpty()) return "waiting for CampTransfer";
        if (text.length() > 90) text = text.substring(0, 90) + "…";
        return text;
    }
}
