package com.mixtervee.camptransferremote;

import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONObject;

import java.util.Arrays;
import java.util.List;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

public class MainActivityV30 extends MainActivityV20 {
    private static final String PREF_CONTROL_TOKEN = "control_token";
    private static final List<String> UPLOAD_LIMITS = Arrays.asList(
            "0.25 Mbps", "0.5 Mbps", "1 Mbps", "2 Mbps", "5 Mbps", "10 Mbps",
            "250 KB/s", "500 KB/s", "1 MB/s", "2 MB/s", "Unlimited");

    private final Handler mainHandler = new Handler(Looper.getMainLooper());
    private SharedPreferences prefsV30;
    private ScheduledExecutorService controlPoller;

    private EditText pairCodeEdit;
    private Button pairButton;
    private TextView pairStatusText;
    private Button startButton;
    private Button pauseResumeButton;
    private Button cancelButton;
    private CheckBox pauseAfterCheck;
    private Spinner uploadSpinner;
    private Button applyUploadButton;
    private TextView commandStatusText;

    private volatile boolean lastPaused;
    private boolean updatingPauseAfter;
    private boolean uploadSelectionPending;

    private int surfaceColor;
    private int surfaceAltColor;
    private int primaryTextColor;
    private int secondaryTextColor;
    private int borderColor;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        prefsV30 = getSharedPreferences(PREFS, MODE_PRIVATE);
        resolvePaletteV30();
        buildRemoteControlsCard();
        refreshPairingUi();
    }

    @Override
    protected void onResume() {
        super.onResume();
        startControlPolling();
    }

    @Override
    protected void onPause() {
        stopControlPolling();
        super.onPause();
    }

    private void resolvePaletteV30() {
        int night = getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK;
        boolean dark = night == Configuration.UI_MODE_NIGHT_YES;
        surfaceColor = dark ? 0xFF171D22 : 0xFFFFFFFF;
        surfaceAltColor = dark ? 0xFF20272D : 0xFFF0F5F7;
        primaryTextColor = dark ? 0xFFF5F7F8 : 0xFF15242B;
        secondaryTextColor = dark ? 0xFFAAB5BA : 0xFF60727B;
        borderColor = dark ? 0xFF303A41 : 0xFFDCE5E9;
    }

    private void buildRemoteControlsCard() {
        LinearLayout root = findRootLayout();
        if (root == null) return;

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(18), dp(16), dp(18), dp(18));
        card.setBackground(roundRect(surfaceColor, 18, borderColor));
        card.setElevation(dp(2));

        TextView eyebrow = label("REMOTE CONTROL", 12, true, secondaryTextColor);
        card.addView(eyebrow);

        TextView helper = label(
                "Pair once, then control CampTransfer automatically over direct Wi-Fi or the home relay.",
                13, false, secondaryTextColor);
        helper.setPadding(0, dp(5), 0, dp(12));
        card.addView(helper);

        LinearLayout pairRow = new LinearLayout(this);
        pairRow.setOrientation(LinearLayout.HORIZONTAL);
        pairRow.setGravity(Gravity.CENTER_VERTICAL);

        pairCodeEdit = new EditText(this);
        pairCodeEdit.setSingleLine(true);
        pairCodeEdit.setHint("6-digit pairing code");
        pairCodeEdit.setInputType(InputType.TYPE_CLASS_NUMBER);
        pairCodeEdit.setTextColor(primaryTextColor);
        pairCodeEdit.setHintTextColor(secondaryTextColor);
        pairCodeEdit.setBackground(roundRect(surfaceAltColor, 12, borderColor));
        pairCodeEdit.setPadding(dp(12), 0, dp(12), 0);
        pairRow.addView(pairCodeEdit, new LinearLayout.LayoutParams(0, dp(48), 1f));

        pairButton = new Button(this);
        pairButton.setText("Pair");
        LinearLayout.LayoutParams pairButtonParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, dp(48));
        pairButtonParams.leftMargin = dp(8);
        pairRow.addView(pairButton, pairButtonParams);
        card.addView(pairRow);

        pairStatusText = label("Not paired", 12, false, secondaryTextColor);
        pairStatusText.setPadding(0, dp(6), 0, dp(12));
        card.addView(pairStatusText);

        LinearLayout primaryButtons = new LinearLayout(this);
        primaryButtons.setOrientation(LinearLayout.HORIZONTAL);

        startButton = new Button(this);
        startButton.setText("Start");
        primaryButtons.addView(startButton, weightedButtonParams(1f, 0));

        pauseResumeButton = new Button(this);
        pauseResumeButton.setText("Pause");
        primaryButtons.addView(pauseResumeButton, weightedButtonParams(1f, dp(8)));
        card.addView(primaryButtons);

        cancelButton = new Button(this);
        cancelButton.setText("Cancel Current");
        LinearLayout.LayoutParams cancelParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        cancelParams.topMargin = dp(8);
        card.addView(cancelButton, cancelParams);

        pauseAfterCheck = new CheckBox(this);
        pauseAfterCheck.setText("Pause after current");
        pauseAfterCheck.setTextColor(primaryTextColor);
        LinearLayout.LayoutParams pauseAfterParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        pauseAfterParams.topMargin = dp(10);
        card.addView(pauseAfterCheck, pauseAfterParams);

        TextView limitLabel = label("UPLOAD LIMIT", 11, true, secondaryTextColor);
        limitLabel.setPadding(0, dp(12), 0, dp(5));
        card.addView(limitLabel);

        LinearLayout uploadRow = new LinearLayout(this);
        uploadRow.setOrientation(LinearLayout.HORIZONTAL);
        uploadRow.setGravity(Gravity.CENTER_VERTICAL);

        uploadSpinner = new Spinner(this);
        ArrayAdapter<String> adapter = new ArrayAdapter<>(
                this, android.R.layout.simple_spinner_item, UPLOAD_LIMITS);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        uploadSpinner.setAdapter(adapter);
        uploadSpinner.setOnTouchListener((v, event) -> {
            uploadSelectionPending = true;
            return false;
        });
        uploadRow.addView(uploadSpinner, new LinearLayout.LayoutParams(0, dp(48), 1f));

        applyUploadButton = new Button(this);
        applyUploadButton.setText("Apply");
        LinearLayout.LayoutParams applyParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT, dp(48));
        applyParams.leftMargin = dp(8);
        uploadRow.addView(applyUploadButton, applyParams);
        card.addView(uploadRow);

        commandStatusText = label("", 12, false, secondaryTextColor);
        commandStatusText.setPadding(0, dp(8), 0, 0);
        card.addView(commandStatusText);

        pairButton.setOnClickListener(v -> {
            if (isPaired()) {
                prefsV30.edit().remove(PREF_CONTROL_TOKEN).apply();
                refreshPairingUi();
                setCommandStatus("Enter the current 6-digit code shown by Pair Remote… in CampTransfer.");
            } else {
                pairControls();
            }
        });
        startButton.setOnClickListener(v -> sendCommand("start", null));
        pauseResumeButton.setOnClickListener(v -> sendCommand(lastPaused ? "resume" : "pause", null));
        cancelButton.setOnClickListener(v -> sendCommand("cancelCurrent", null));
        pauseAfterCheck.setOnClickListener(v -> {
            if (!updatingPauseAfter)
                sendCommand("pauseAfterCurrent", pauseAfterCheck.isChecked());
        });
        applyUploadButton.setOnClickListener(v -> {
            Object selected = uploadSpinner.getSelectedItem();
            if (selected != null) sendCommand("setUploadLimit", selected.toString());
        });

        LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        cardParams.topMargin = dp(18);

        int insertIndex = Math.min(3, root.getChildCount());
        root.addView(card, insertIndex, cardParams);
    }

    private void pairControls() {
        String code = pairCodeEdit.getText().toString().trim();
        if (code.length() != 6) {
            Toast.makeText(this, "Enter the 6-digit code shown in CampTransfer.", Toast.LENGTH_SHORT).show();
            return;
        }

        setCommandStatus("Pairing…");
        pairButton.setEnabled(false);
        new Thread(() -> {
            try {
                String direct = prefsV30.getString(PREF_HOST, "");
                String relay = prefsV30.getString(PREF_RELAY_HOST, "");
                CampTransferClient.PairResult result = CampTransferClient.pairBest(direct, relay, code);
                prefsV30.edit().putString(PREF_CONTROL_TOKEN, result.token).apply();
                mainHandler.post(() -> {
                    pairCodeEdit.setText("");
                    pairButton.setEnabled(true);
                    refreshPairingUi();
                    setCommandStatus(result.message);
                    Toast.makeText(this, "Remote control paired", Toast.LENGTH_SHORT).show();
                    pollControlState();
                });
            } catch (Exception ex) {
                mainHandler.post(() -> {
                    pairButton.setEnabled(true);
                    setCommandStatus(friendly(ex));
                });
            }
        }).start();
    }

    private void sendCommand(String action, Object value) {
        String token = prefsV30.getString(PREF_CONTROL_TOKEN, "");
        if (token == null || token.isEmpty()) {
            setCommandStatus("Pair remote control first.");
            return;
        }

        setControlsEnabled(false);
        setCommandStatus("Sending command…");
        new Thread(() -> {
            try {
                String direct = prefsV30.getString(PREF_HOST, "");
                String relay = prefsV30.getString(PREF_RELAY_HOST, "");
                CampTransferClient.ControlResult result = CampTransferClient.sendBestControl(
                        direct, relay, token, action, value);
                mainHandler.post(() -> {
                    if ("setUploadLimit".equals(action) && result.ok)
                        uploadSelectionPending = false;
                    setCommandStatus(result.message);
                    Toast.makeText(this, result.message, Toast.LENGTH_SHORT).show();
                    pollControlState();
                });
            } catch (SecurityException ex) {
                prefsV30.edit().remove(PREF_CONTROL_TOKEN).apply();
                mainHandler.post(() -> {
                    refreshPairingUi();
                    setCommandStatus("Pairing expired. Enter the current code from CampTransfer.");
                });
            } catch (Exception ex) {
                mainHandler.post(() -> {
                    setCommandStatus(friendly(ex));
                    pollControlState();
                });
            }
        }).start();
    }

    private void startControlPolling() {
        stopControlPolling();
        controlPoller = Executors.newSingleThreadScheduledExecutor();
        controlPoller.scheduleAtFixedRate(this::pollControlState, 0, 2, TimeUnit.SECONDS);
    }

    private void stopControlPolling() {
        if (controlPoller != null) {
            controlPoller.shutdownNow();
            controlPoller = null;
        }
    }

    private void pollControlState() {
        if (prefsV30 == null) return;
        String direct = prefsV30.getString(PREF_HOST, "");
        String relay = prefsV30.getString(PREF_RELAY_HOST, "");
        if ((direct == null || direct.trim().isEmpty()) && (relay == null || relay.trim().isEmpty())) return;

        try {
            CampTransferClient.StatusResult result = CampTransferClient.fetchBestStatus(direct, relay);
            JSONObject status = result.status;
            JSONObject active = status.optJSONObject("active");
            String state = status.optString("state", "");
            boolean paused = "Paused".equalsIgnoreCase(state) ||
                    (active != null && "Paused".equalsIgnoreCase(active.optString("status", "")));
            boolean hasActive = active != null;
            int filesLeft = status.optInt("filesLeft", 0);
            boolean pauseAfter = status.optBoolean("pauseAfterCurrent", false);
            String uploadLimit = status.optString("uploadLimit", "");
            boolean controlAvailable = status.optBoolean("remoteControlAvailable", true);

            mainHandler.post(() -> updateControlState(
                    paused, hasActive, filesLeft, pauseAfter, uploadLimit, controlAvailable));
        } catch (Exception ignored) {
        }
    }

    private void updateControlState(
            boolean paused,
            boolean hasActive,
            int filesLeft,
            boolean pauseAfter,
            String uploadLimit,
            boolean controlAvailable) {
        lastPaused = paused;
        pauseResumeButton.setText(paused ? "Resume" : "Pause");

        updatingPauseAfter = true;
        pauseAfterCheck.setChecked(pauseAfter);
        updatingPauseAfter = false;

        if (!uploadSelectionPending) {
            int index = UPLOAD_LIMITS.indexOf(uploadLimit);
            if (index >= 0 && uploadSpinner.getSelectedItemPosition() != index)
                uploadSpinner.setSelection(index);
        }

        if (!controlAvailable) {
            pairStatusText.setText("Remote control is not available on the connected CampTransfer build.");
            setControlsEnabled(false);
            return;
        }

        refreshPairingUi();
        boolean paired = isPaired();
        startButton.setEnabled(paired && !hasActive && filesLeft > 0);
        pauseResumeButton.setEnabled(paired && hasActive);
        cancelButton.setEnabled(paired && hasActive);
        pauseAfterCheck.setEnabled(paired);
        uploadSpinner.setEnabled(paired);
        applyUploadButton.setEnabled(paired);
    }

    private void refreshPairingUi() {
        if (pairStatusText == null) return;
        boolean paired = isPaired();
        pairStatusText.setText(paired
                ? "Paired • controls follow the same automatic direct/relay connection"
                : "Not paired • click Pair Remote… in CampTransfer to get the code");
        pairCodeEdit.setVisibility(paired ? View.GONE : View.VISIBLE);
        pairButton.setText(paired ? "Re-pair" : "Pair");
        if (paired) pairCodeEdit.setText("");
        if (!paired) setControlsEnabled(false);
    }

    private boolean isPaired() {
        String token = prefsV30 == null ? "" : prefsV30.getString(PREF_CONTROL_TOKEN, "");
        return token != null && !token.isEmpty();
    }

    private void setControlsEnabled(boolean enabled) {
        startButton.setEnabled(enabled);
        pauseResumeButton.setEnabled(enabled);
        cancelButton.setEnabled(enabled);
        pauseAfterCheck.setEnabled(enabled);
        uploadSpinner.setEnabled(enabled);
        applyUploadButton.setEnabled(enabled);
    }

    private void setCommandStatus(String text) {
        if (commandStatusText != null) commandStatusText.setText(text == null ? "" : text);
    }

    private LinearLayout findRootLayout() {
        View content = findViewById(android.R.id.content);
        ScrollView scroll = findFirst(content, ScrollView.class);
        if (scroll == null || scroll.getChildCount() == 0) return null;
        View child = scroll.getChildAt(0);
        return child instanceof LinearLayout ? (LinearLayout) child : null;
    }

    private <T extends View> T findFirst(View root, Class<T> cls) {
        if (cls.isInstance(root)) return cls.cast(root);
        if (root instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) root;
            for (int i = 0; i < group.getChildCount(); i++) {
                T found = findFirst(group.getChildAt(i), cls);
                if (found != null) return found;
            }
        }
        return null;
    }

    private LinearLayout.LayoutParams weightedButtonParams(float weight, int leftMargin) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.WRAP_CONTENT, weight);
        params.leftMargin = leftMargin;
        return params;
    }

    private TextView label(String text, int sp, boolean bold, int color) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setTextSize(sp);
        view.setTextColor(color);
        if (bold) view.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return view;
    }

    private GradientDrawable roundRect(int fill, int radiusDp, int stroke) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(fill);
        drawable.setCornerRadius(dp(radiusDp));
        drawable.setStroke(dp(1), stroke);
        return drawable;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private String friendly(Exception ex) {
        String message = ex.getMessage();
        if ((message == null || message.trim().isEmpty()) && ex.getCause() != null)
            message = ex.getCause().getMessage();
        return message == null || message.trim().isEmpty() ? "Command failed" : message;
    }
}
