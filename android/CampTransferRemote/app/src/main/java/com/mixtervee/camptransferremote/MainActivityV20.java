package com.mixtervee.camptransferremote;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.content.res.Configuration;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
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

public class MainActivityV20 extends Activity {
    static final String PREFS = "camptransfer_remote";
    static final String PREF_HOST = "host";
    static final String PREF_RELAY_HOST = "relay_host";

    private static final int CYAN = 0xFF26C6DA;
    private static final int CYAN_DARK = 0xFF0AA9BE;
    private static final int GREEN = 0xFF48C78E;
    private static final int AMBER = 0xFFFFB74D;
    private static final int RED = 0xFFEF6C72;

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private SharedPreferences prefs;
    private ScheduledExecutorService poller;
    private boolean connectionPanelPinnedOpen;

    private boolean darkMode;
    private int pageColor;
    private int surfaceColor;
    private int surfaceAltColor;
    private int primaryTextColor;
    private int secondaryTextColor;
    private int borderColor;

    private EditText hostEdit;
    private EditText relayEdit;
    private TextView connectionChip;
    private LinearLayout connectionPanel;
    private Button discoverButton;

    private TextView sectionEyebrow;
    private TextView activeFileText;
    private TextView operationBadge;
    private TextView statusBadge;
    private TextView progressText;
    private ProgressBar progressBar;
    private TextView speedValueText;
    private TextView etaValueText;
    private TextView uploadLimitValueText;
    private TextView destinationText;
    private TextView finishActionText;

    private TextView queueSummaryText;
    private LinearLayout queueContainer;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        prefs = getSharedPreferences(PREFS, MODE_PRIVATE);
        resolvePalette();
        buildUi();
        requestNotificationPermissionIfNeeded();

        String savedHost = prefs.getString(PREF_HOST, "");
        String savedRelay = prefs.getString(PREF_RELAY_HOST, "");
        hostEdit.setText(savedHost == null ? "" : savedHost);
        relayEdit.setText(savedRelay == null ? "" : savedRelay);

        if (savedHost == null || savedHost.trim().isEmpty()) {
            connectionPanelPinnedOpen = true;
            setConnectionPanelVisible(true);
            discoverPc();
        } else {
            setConnectionPanelVisible(false);
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

    private void resolvePalette() {
        int nightMode = getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK;
        darkMode = nightMode == Configuration.UI_MODE_NIGHT_YES;

        pageColor = darkMode ? 0xFF0E1216 : 0xFFF4F7F9;
        surfaceColor = darkMode ? 0xFF171D22 : 0xFFFFFFFF;
        surfaceAltColor = darkMode ? 0xFF20272D : 0xFFF0F5F7;
        primaryTextColor = darkMode ? 0xFFF5F7F8 : 0xFF15242B;
        secondaryTextColor = darkMode ? 0xFFAAB5BA : 0xFF60727B;
        borderColor = darkMode ? 0xFF303A41 : 0xFFDCE5E9;

        getWindow().setStatusBarColor(pageColor);
        getWindow().setNavigationBarColor(pageColor);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            int flags = getWindow().getDecorView().getSystemUiVisibility();
            if (!darkMode) flags |= View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
            else flags &= ~View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
            getWindow().getDecorView().setSystemUiVisibility(flags);
        }
    }

    private void buildUi() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(pageColor);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(18), dp(18), dp(18), dp(28));
        scroll.addView(root, new ScrollView.LayoutParams(
                ScrollView.LayoutParams.MATCH_PARENT,
                ScrollView.LayoutParams.WRAP_CONTENT));

        buildHeader(root);
        buildConnectionPanel(root);
        buildCurrentTransferCard(root);
        buildQueueSection(root);

        TextView footer = text("CampTransfer Remote  •  Automatic direct + relay", 12, false, secondaryTextColor);
        footer.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams footerParams = matchWrap();
        footerParams.topMargin = dp(24);
        root.addView(footer, footerParams);

        setContentView(scroll);
    }

    private void buildHeader(LinearLayout root) {
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);

        LinearLayout titleBlock = new LinearLayout(this);
        titleBlock.setOrientation(LinearLayout.VERTICAL);

        TextView title = text("CampTransfer", 27, true, primaryTextColor);
        titleBlock.addView(title);

        TextView subtitle = text("Remote monitor", 14, false, secondaryTextColor);
        subtitle.setPadding(0, dp(1), 0, 0);
        titleBlock.addView(subtitle);

        header.addView(titleBlock, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        connectionChip = text("●  Connecting", 13, true, primaryTextColor);
        connectionChip.setGravity(Gravity.CENTER);
        connectionChip.setPadding(dp(13), dp(8), dp(13), dp(8));
        connectionChip.setBackground(roundRect(surfaceAltColor, 99, borderColor, 1));
        connectionChip.setOnClickListener(v -> {
            boolean show = connectionPanel.getVisibility() != View.VISIBLE;
            connectionPanelPinnedOpen = show;
            setConnectionPanelVisible(show);
        });
        header.addView(connectionChip);

        root.addView(header, matchWrap());
    }

    private void buildConnectionPanel(LinearLayout root) {
        connectionPanel = new LinearLayout(this);
        connectionPanel.setOrientation(LinearLayout.VERTICAL);
        connectionPanel.setPadding(dp(16), dp(15), dp(16), dp(16));
        connectionPanel.setBackground(roundRect(surfaceColor, 18, borderColor, 1));
        connectionPanel.setElevation(dp(1));

        TextView heading = text("CONNECTION", 12, true, secondaryTextColor);
        connectionPanel.addView(heading);

        TextView helper = text(
                "Automatic: CampTransfer Remote uses the laptop directly when available, then falls back to your home relay without you changing modes.",
                13, false, secondaryTextColor);
        helper.setPadding(0, dp(5), 0, dp(12));
        connectionPanel.addView(helper);

        TextView laptopLabel = text("Camp laptop", 12, true, secondaryTextColor);
        connectionPanel.addView(laptopLabel);

        hostEdit = new EditText(this);
        hostEdit.setSingleLine(true);
        hostEdit.setTextColor(primaryTextColor);
        hostEdit.setHintTextColor(secondaryTextColor);
        hostEdit.setHint("Laptop IP address or hostname");
        hostEdit.setBackground(roundRect(surfaceAltColor, 12, borderColor, 1));
        hostEdit.setPadding(dp(12), 0, dp(12), 0);
        LinearLayout.LayoutParams hostParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(48));
        hostParams.topMargin = dp(5);
        connectionPanel.addView(hostEdit, hostParams);

        TextView relayLabel = text("Home relay", 12, true, secondaryTextColor);
        LinearLayout.LayoutParams relayLabelParams = matchWrap();
        relayLabelParams.topMargin = dp(12);
        connectionPanel.addView(relayLabel, relayLabelParams);

        relayEdit = new EditText(this);
        relayEdit.setSingleLine(true);
        relayEdit.setTextColor(primaryTextColor);
        relayEdit.setHintTextColor(secondaryTextColor);
        relayEdit.setHint("Home server IP address or hostname");
        relayEdit.setBackground(roundRect(surfaceAltColor, 12, borderColor, 1));
        relayEdit.setPadding(dp(12), 0, dp(12), 0);
        LinearLayout.LayoutParams relayParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(48));
        relayParams.topMargin = dp(5);
        connectionPanel.addView(relayEdit, relayParams);

        Button saveButton = button("Save connection settings", true);
        saveButton.setOnClickListener(v -> saveConnectionSettings());
        LinearLayout.LayoutParams saveParams = matchWrap();
        saveParams.topMargin = dp(10);
        connectionPanel.addView(saveButton, saveParams);

        discoverButton = button("Discover laptop on Wi-Fi", false);
        discoverButton.setOnClickListener(v -> discoverPc());
        LinearLayout.LayoutParams discoverParams = matchWrap();
        discoverParams.topMargin = dp(8);
        connectionPanel.addView(discoverButton, discoverParams);

        TextView relayHint = text("Relay uses TCP " + CampTransferClient.RELAY_PORT + " on the home server.",
                11, false, secondaryTextColor);
        relayHint.setPadding(0, dp(8), 0, 0);
        connectionPanel.addView(relayHint);

        LinearLayout.LayoutParams panelParams = matchWrap();
        panelParams.topMargin = dp(15);
        root.addView(connectionPanel, panelParams);
    }

    private void buildCurrentTransferCard(LinearLayout root) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(18), dp(17), dp(18), dp(18));
        card.setBackground(roundRect(surfaceColor, 20, borderColor, 1));
        card.setElevation(dp(2));

        sectionEyebrow = text("CURRENT TRANSFER", 12, true, CYAN_DARK);
        card.addView(sectionEyebrow);

        activeFileText = text("Waiting for CampTransfer", 21, true, primaryTextColor);
        activeFileText.setPadding(0, dp(8), 0, dp(10));
        card.addView(activeFileText);

        LinearLayout badges = new LinearLayout(this);
        badges.setOrientation(LinearLayout.HORIZONTAL);
        badges.setGravity(Gravity.CENTER_VERTICAL);

        operationBadge = badge("COPY", CYAN_DARK, darkMode ? 0xFF12353A : 0xFFE4F9FB);
        badges.addView(operationBadge);

        statusBadge = badge("CONNECTING", secondaryTextColor, surfaceAltColor);
        LinearLayout.LayoutParams statusParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        statusParams.leftMargin = dp(7);
        badges.addView(statusBadge, statusParams);
        card.addView(badges);

        LinearLayout progressRow = new LinearLayout(this);
        progressRow.setOrientation(LinearLayout.HORIZONTAL);
        progressRow.setGravity(Gravity.BOTTOM);
        progressRow.setPadding(0, dp(16), 0, dp(8));

        progressText = text("0.0%", 32, true, primaryTextColor);
        progressRow.addView(progressText, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        finishActionText = text("When finished: —", 12, false, secondaryTextColor);
        finishActionText.setGravity(Gravity.RIGHT | Gravity.BOTTOM);
        progressRow.addView(finishActionText);
        card.addView(progressRow);

        progressBar = makeProgressBar(dp(10));
        card.addView(progressBar, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(10)));

        LinearLayout stats = new LinearLayout(this);
        stats.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams statsParams = matchWrap();
        statsParams.topMargin = dp(14);

        speedValueText = addStatTile(stats, "SPEED", "—", 1.2f);
        etaValueText = addStatTile(stats, "ETA", "—", 0.8f);
        card.addView(stats, statsParams);

        LinearLayout uploadStrip = new LinearLayout(this);
        uploadStrip.setOrientation(LinearLayout.HORIZONTAL);
        uploadStrip.setGravity(Gravity.CENTER_VERTICAL);
        uploadStrip.setPadding(dp(14), dp(11), dp(14), dp(12));
        uploadStrip.setBackground(roundRect(surfaceAltColor, 14, 0, 0));
        LinearLayout.LayoutParams uploadParams = matchWrap();
        uploadParams.topMargin = dp(9);

        TextView uploadLabel = text("UPLOAD LIMIT", 11, true, secondaryTextColor);
        uploadStrip.addView(uploadLabel, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        uploadLimitValueText = text("—", 20, true, primaryTextColor);
        uploadLimitValueText.setGravity(Gravity.END | Gravity.CENTER_VERTICAL);
        uploadStrip.addView(uploadLimitValueText);
        card.addView(uploadStrip, uploadParams);

        destinationText = text("Destination will appear here", 13, false, secondaryTextColor);
        destinationText.setPadding(0, dp(14), 0, 0);
        destinationText.setMaxLines(2);
        card.addView(destinationText);

        LinearLayout.LayoutParams cardParams = matchWrap();
        cardParams.topMargin = dp(18);
        root.addView(card, cardParams);
    }

    private void buildQueueSection(LinearLayout root) {
        LinearLayout headingRow = new LinearLayout(this);
        headingRow.setOrientation(LinearLayout.HORIZONTAL);
        headingRow.setGravity(Gravity.CENTER_VERTICAL);

        TextView queueHeading = text("Queue", 20, true, primaryTextColor);
        headingRow.addView(queueHeading, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        queueSummaryText = text("Waiting for status", 13, false, secondaryTextColor);
        queueSummaryText.setGravity(Gravity.RIGHT);
        headingRow.addView(queueSummaryText);

        LinearLayout.LayoutParams headingParams = matchWrap();
        headingParams.topMargin = dp(22);
        root.addView(headingRow, headingParams);

        queueContainer = new LinearLayout(this);
        queueContainer.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams queueParams = matchWrap();
        queueParams.topMargin = dp(10);
        root.addView(queueContainer, queueParams);

        showQueuePlaceholder("Waiting for CampTransfer…");
    }

    private TextView addStatTile(LinearLayout parent, String label, String value, float weight) {
        LinearLayout tile = new LinearLayout(this);
        tile.setOrientation(LinearLayout.VERTICAL);
        tile.setPadding(dp(14), dp(11), dp(14), dp(12));
        tile.setBackground(roundRect(surfaceAltColor, 14, 0, 0));

        TextView labelView = text(label, 11, true, secondaryTextColor);
        tile.addView(labelView);

        TextView valueView = text(value, 20, true, primaryTextColor);
        valueView.setPadding(0, dp(3), 0, 0);
        valueView.setSingleLine(false);
        tile.addView(valueView);

        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, weight);
        if (parent.getChildCount() > 0) params.leftMargin = dp(9);
        parent.addView(tile, params);
        return valueView;
    }

    private void saveConnectionSettings() {
        String host = CampTransferClient.normalizeHost(hostEdit.getText().toString());
        String relay = CampTransferClient.normalizeHost(relayEdit.getText().toString());
        hostEdit.setText(host);
        relayEdit.setText(relay);
        prefs.edit()
                .putString(PREF_HOST, host)
                .putString(PREF_RELAY_HOST, relay)
                .apply();

        if (host.isEmpty() && relay.isEmpty()) {
            setConnectionChip("●  Enter an address", AMBER, false);
            connectionPanelPinnedOpen = true;
            setConnectionPanelVisible(true);
            return;
        }

        setConnectionChip("●  Connecting", AMBER, false);
        connectionPanelPinnedOpen = false;
        pollOnce();
    }

    private void discoverPc() {
        discoverButton.setEnabled(false);
        discoverButton.setText("Looking…");
        setConnectionChip("●  Discovering", AMBER, false);
        new Thread(() -> {
            try {
                CampTransferClient.DiscoveredPc pc = CampTransferClient.discover();
                prefs.edit().putString(PREF_HOST, pc.host).apply();
                mainHandler.post(() -> {
                    hostEdit.setText(pc.host);
                    discoverButton.setText("Discover laptop on Wi-Fi");
                    discoverButton.setEnabled(true);
                    connectionPanelPinnedOpen = false;
                    setConnectionChip("●  Found " + pc.pcName, GREEN, true);
                    pollOnce();
                });
            } catch (Exception ex) {
                mainHandler.post(() -> {
                    discoverButton.setText("Discover laptop on Wi-Fi");
                    discoverButton.setEnabled(true);
                    String direct = prefs.getString(PREF_HOST, "");
                    String relay = prefs.getString(PREF_RELAY_HOST, "");
                    if ((direct != null && !direct.isEmpty()) || (relay != null && !relay.isEmpty())) {
                        setConnectionChip("●  Trying saved connection", AMBER, false);
                        pollOnce();
                    } else {
                        setConnectionChip("●  Not connected", RED, false);
                        connectionPanelPinnedOpen = true;
                        setConnectionPanelVisible(true);
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
        String direct = prefs.getString(PREF_HOST, "");
        String relay = prefs.getString(PREF_RELAY_HOST, "");
        if ((direct == null || direct.trim().isEmpty()) && (relay == null || relay.trim().isEmpty())) return;

        try {
            CampTransferClient.StatusResult result = CampTransferClient.fetchBestStatus(direct, relay);
            mainHandler.post(() -> applyStatus(result));
        } catch (Exception ex) {
            final String directDisplay = direct == null ? "" : direct.trim();
            final String relayDisplay = relay == null ? "" : relay.trim();
            mainHandler.post(() -> {
                setConnectionChip("●  Offline", RED, false);
                sectionEyebrow.setText("CONNECTION LOST");
                sectionEyebrow.setTextColor(RED);
                statusBadge.setText("WAITING");
                styleBadge(statusBadge, RED, darkMode ? 0xFF3A2022 : 0xFFFFECEE);
                if (!directDisplay.isEmpty() && !relayDisplay.isEmpty())
                    queueSummaryText.setText("Trying direct + relay");
                else if (!relayDisplay.isEmpty())
                    queueSummaryText.setText("Trying relay " + relayDisplay);
                else
                    queueSummaryText.setText("Trying " + directDisplay);
            });
        }
    }

    private void applyStatus(CampTransferClient.StatusResult result) {
        JSONObject status = result.status;
        String pcName = status.optString("pcName", result.sourceHost);
        String state = status.optString("state", "Ready");
        int filesLeft = status.optInt("filesLeft", 0);
        String whenFinished = status.optString("whenFinished", "Do nothing");
        String uploadLimit = status.optString("uploadLimit", "—");
        JSONArray queue = status.optJSONArray("queue");

        boolean relayStale = false;
        double relayAge = 0;
        String relayName = result.sourceHost;
        if (result.viaRelay) {
            JSONObject relay = status.optJSONObject("relay");
            if (relay != null) {
                relayStale = relay.optBoolean("stale", false);
                relayAge = relay.optDouble("ageSeconds", 0);
                relayName = relay.optString("serverName", result.sourceHost);
            }

            if (relayStale)
                setConnectionChip("●  Relay stale • " + relayName, AMBER, false);
            else
                setConnectionChip("●  Relay via " + relayName, GREEN, true);
        } else {
            setConnectionChip("●  Connected to " + pcName, GREEN, true);
        }

        if (!connectionPanelPinnedOpen) setConnectionPanelVisible(false);
        finishActionText.setText("When finished: " + whenFinished);
        uploadLimitValueText.setText(uploadLimit == null || uploadLimit.trim().isEmpty() || "Unknown".equalsIgnoreCase(uploadLimit)
                ? "—" : uploadLimit);

        JSONObject active = status.optJSONObject("active");
        String activeId = active == null ? "" : active.optString("id", "");

        if (active == null) {
            boolean hasQueue = queue != null && queue.length() > 0;
            boolean queueComplete = hasQueue && filesLeft == 0;

            sectionEyebrow.setText(queueComplete ? "ALL DONE" : relayStale ? "LAST RELAY UPDATE" : "CAMPTRANSFER STATUS");
            sectionEyebrow.setTextColor(queueComplete ? GREEN : relayStale ? AMBER : CYAN_DARK);
            activeFileText.setText(queueComplete ? "Queue complete" : filesLeft > 0 ? "Waiting for next file" : "Nothing transferring");
            operationBadge.setText("IDLE");
            styleBadge(operationBadge, secondaryTextColor, surfaceAltColor);
            statusBadge.setText(queueComplete ? "COMPLETED" : compactStatus(state).toUpperCase(Locale.getDefault()));
            styleStatusBadge(statusBadge, state, queueComplete);
            progressBar.setProgress(queueComplete ? 1000 : 0);
            progressText.setText(queueComplete ? "100%" : "0.0%");
            speedValueText.setText("—");
            etaValueText.setText("—");
            destinationText.setText(queueComplete
                    ? "Everything in this queue finished successfully."
                    : relayStale
                        ? "Showing the most recent relay snapshot while waiting for CampTransfer to report again."
                        : "Start or resume a transfer on the laptop to see live details here.");
        } else {
            String name = active.optString("fileName", "Transfer");
            String operation = active.optString("operation", "Copy");
            double percent = active.optDouble("progressPercent", 0d);
            String speed = active.optString("speed", "");
            String eta = active.optString("eta", "");
            String itemStatus = active.optString("status", "Transferring");
            String destination = active.optString("destination", "");

            sectionEyebrow.setText(relayStale ? "LAST RELAY UPDATE" : "CURRENT TRANSFER");
            sectionEyebrow.setTextColor(relayStale ? AMBER : CYAN_DARK);
            activeFileText.setText(name);
            operationBadge.setText(operation.toUpperCase(Locale.getDefault()));
            styleBadge(operationBadge, CYAN_DARK, darkMode ? 0xFF12353A : 0xFFE4F9FB);
            statusBadge.setText(compactStatus(itemStatus).toUpperCase(Locale.getDefault()));
            styleStatusBadge(statusBadge, itemStatus, false);

            progressBar.setProgress((int) Math.max(0, Math.min(1000, Math.round(percent * 10))));
            progressText.setText(String.format(Locale.getDefault(), "%.1f%%", percent));
            speedValueText.setText(blankAsDash(speed));
            etaValueText.setText(blankAsDash(eta));
            destinationText.setText(destination.isEmpty() ? "Destination unavailable" : "To  " + destination);
        }

        renderQueue(queue, activeId, filesLeft);
        if (relayStale)
            queueSummaryText.setText("Relay update " + formatAge(relayAge) + " ago");

        if (filesLeft > 0) startBackgroundMonitor();
    }

    private void renderQueue(JSONArray queue, String activeId, int filesLeft) {
        queueContainer.removeAllViews();

        if (queue == null || queue.length() == 0) {
            queueSummaryText.setText("Empty");
            showQueuePlaceholder("No files in the queue.");
            return;
        }

        int completed = 0;
        for (int i = 0; i < queue.length(); i++) {
            JSONObject item = queue.optJSONObject(i);
            if (item != null && item.optBoolean("completed", false)) completed++;
        }

        if (filesLeft == 0)
            queueSummaryText.setText(completed + " completed");
        else
            queueSummaryText.setText(filesLeft + (filesLeft == 1 ? " file left" : " files left"));

        for (int i = 0; i < queue.length(); i++) {
            JSONObject item = queue.optJSONObject(i);
            if (item == null) continue;
            boolean isActive = !activeId.isEmpty() && activeId.equals(item.optString("id", ""));
            addQueueCard(item, isActive);
        }
    }

    private void addQueueCard(JSONObject item, boolean isActive) {
        boolean complete = item.optBoolean("completed", false);
        boolean cleanupPending = item.optBoolean("sourceCleanupPending", false);
        String operation = item.optString("operation", "Copy");
        String status = item.optString("status", "Queued");
        String fileName = item.optString("fileName", "File");
        double percent = item.optDouble("progressPercent", 0d);

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(14), dp(12), dp(14), dp(13));
        int stroke = isActive ? CYAN_DARK : borderColor;
        card.setBackground(roundRect(complete ? surfaceAltColor : surfaceColor, 15, stroke, isActive ? 2 : 1));
        if (isActive) card.setElevation(dp(2));

        LinearLayout topRow = new LinearLayout(this);
        topRow.setOrientation(LinearLayout.HORIZONTAL);
        topRow.setGravity(Gravity.CENTER_VERTICAL);

        TextView marker = text(complete ? "✓" : isActive ? "▶" : cleanupPending ? "!" : "○",
                15, true, complete ? GREEN : isActive ? CYAN_DARK : cleanupPending ? AMBER : secondaryTextColor);
        marker.setGravity(Gravity.CENTER);
        topRow.addView(marker, new LinearLayout.LayoutParams(dp(24), LinearLayout.LayoutParams.WRAP_CONTENT));

        TextView name = text(fileName, 15, true, complete ? secondaryTextColor : primaryTextColor);
        name.setMaxLines(2);
        topRow.addView(name, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));

        TextView mode = badge(operation.toUpperCase(Locale.getDefault()),
                isActive ? CYAN_DARK : secondaryTextColor,
                isActive ? (darkMode ? 0xFF12353A : 0xFFE4F9FB) : surfaceAltColor);
        topRow.addView(mode);
        card.addView(topRow);

        ProgressBar miniProgress = makeProgressBar(dp(5));
        miniProgress.setProgress((int) Math.max(0, Math.min(1000, Math.round(percent * 10))));
        LinearLayout.LayoutParams progressParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, dp(5));
        progressParams.topMargin = dp(10);
        card.addView(miniProgress, progressParams);

        LinearLayout metaRow = new LinearLayout(this);
        metaRow.setOrientation(LinearLayout.HORIZONTAL);
        metaRow.setGravity(Gravity.CENTER_VERTICAL);
        metaRow.setPadding(0, dp(7), 0, 0);

        TextView pct = text(String.format(Locale.getDefault(), "%.1f%%", percent), 12, true,
                complete ? GREEN : isActive ? CYAN_DARK : secondaryTextColor);
        metaRow.addView(pct);

        TextView statusText = text("  •  " + compactStatus(status), 12, false,
                statusLooksProblematic(status) ? (startsWithIgnoreCase(status, "Retrying") ? AMBER : RED) : secondaryTextColor);
        metaRow.addView(statusText, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f));
        card.addView(metaRow);

        LinearLayout.LayoutParams cardParams = matchWrap();
        if (queueContainer.getChildCount() > 0) cardParams.topMargin = dp(8);
        queueContainer.addView(card, cardParams);
    }

    private void showQueuePlaceholder(String message) {
        TextView empty = text(message, 14, false, secondaryTextColor);
        empty.setGravity(Gravity.CENTER);
        empty.setPadding(dp(16), dp(20), dp(16), dp(20));
        empty.setBackground(roundRect(surfaceColor, 15, borderColor, 1));
        queueContainer.addView(empty, matchWrap());
    }

    private void setConnectionChip(String value, int accentColor, boolean connected) {
        connectionChip.setText(value);
        connectionChip.setTextColor(connected ? (darkMode ? 0xFFDDF9E9 : 0xFF175A39) : accentColor);
        int fill = connected
                ? (darkMode ? 0xFF173326 : 0xFFE8F8EF)
                : surfaceAltColor;
        connectionChip.setBackground(roundRect(fill, 99, connected ? GREEN : borderColor, 1));
    }

    private void setConnectionPanelVisible(boolean visible) {
        if (connectionPanel == null) return;
        connectionPanel.setVisibility(visible ? View.VISIBLE : View.GONE);
    }

    private void styleStatusBadge(TextView badge, String status, boolean complete) {
        String lower = status == null ? "" : status.toLowerCase(Locale.ROOT);
        if (complete || lower.equals("completed") || lower.equals("queue complete"))
            styleBadge(badge, GREEN, darkMode ? 0xFF173326 : 0xFFE8F8EF);
        else if (lower.startsWith("retrying") || lower.contains("paused"))
            styleBadge(badge, AMBER, darkMode ? 0xFF382D1D : 0xFFFFF3DD);
        else if (lower.contains("error") || lower.contains("cleanup pending") || lower.contains("cancelled"))
            styleBadge(badge, RED, darkMode ? 0xFF3A2022 : 0xFFFFECEE);
        else
            styleBadge(badge, CYAN_DARK, darkMode ? 0xFF12353A : 0xFFE4F9FB);
    }

    private void styleBadge(TextView view, int textColor, int fillColor) {
        view.setTextColor(textColor);
        view.setBackground(roundRect(fillColor, 99, 0, 0));
    }

    private boolean statusLooksProblematic(String status) {
        String lower = status == null ? "" : status.toLowerCase(Locale.ROOT);
        return lower.startsWith("retrying") || lower.contains("error") ||
                lower.contains("cleanup pending") || lower.contains("cancelled");
    }

    private String compactStatus(String status) {
        if (status == null || status.trim().isEmpty()) return "Queued";
        if (startsWithIgnoreCase(status, "Source cleanup pending")) return "Source cleanup pending";
        if (startsWithIgnoreCase(status, "Retrying")) return status;
        int colon = status.indexOf(':');
        if (colon > 0 && colon < 24) return status.substring(0, colon);
        return status;
    }

    private static boolean startsWithIgnoreCase(String value, String prefix) {
        if (value == null || prefix == null || value.length() < prefix.length()) return false;
        return value.regionMatches(true, 0, prefix, 0, prefix.length());
    }

    private static String formatAge(double seconds) {
        if (seconds < 2) return "just now";
        if (seconds < 60) return Math.round(seconds) + "s";
        long whole = Math.round(seconds);
        return (whole / 60) + "m " + (whole % 60) + "s";
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

    private ProgressBar makeProgressBar(int height) {
        ProgressBar bar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        bar.setMax(1000);
        bar.setProgress(0);
        bar.setProgressTintList(ColorStateList.valueOf(CYAN));
        bar.setProgressBackgroundTintList(ColorStateList.valueOf(darkMode ? 0xFF2B353B : 0xFFDDE8EC));
        bar.setMinimumHeight(height);
        return bar;
    }

    private TextView badge(String value, int textColor, int fillColor) {
        TextView view = text(value, 11, true, textColor);
        view.setGravity(Gravity.CENTER);
        view.setPadding(dp(10), dp(5), dp(10), dp(5));
        view.setBackground(roundRect(fillColor, 99, 0, 0));
        return view;
    }

    private Button button(String value, boolean primary) {
        Button button = new Button(this);
        button.setText(value);
        button.setTextSize(13);
        button.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        button.setAllCaps(false);
        button.setPadding(dp(14), 0, dp(14), 0);
        button.setTextColor(primary ? 0xFF062D33 : primaryTextColor);
        button.setBackground(roundRect(primary ? CYAN : surfaceAltColor, 12, primary ? CYAN : borderColor, 1));
        button.setElevation(0);
        return button;
    }

    private TextView text(String value, int sp, boolean bold, int color) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(sp);
        view.setTextColor(color);
        if (bold) view.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return view;
    }

    private GradientDrawable roundRect(int fillColor, int radiusDp, int strokeColor, int strokeWidthDp) {
        GradientDrawable background = new GradientDrawable();
        background.setColor(fillColor);
        background.setCornerRadius(dp(radiusDp));
        if (strokeWidthDp > 0) background.setStroke(dp(strokeWidthDp), strokeColor);
        return background;
    }

    private LinearLayout.LayoutParams matchWrap() {
        return new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private static String blankAsDash(String value) {
        return value == null || value.trim().isEmpty() ? "—" : value;
    }
}
