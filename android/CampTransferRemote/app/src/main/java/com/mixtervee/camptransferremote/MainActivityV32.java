package com.mixtervee.camptransferremote;

import android.content.SharedPreferences;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.view.ViewGroup;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

public class MainActivityV32 extends MainActivityV31 {
    private final Handler recoveryUi = new Handler(Looper.getMainLooper());
    private ScheduledExecutorService recoveryPoller;
    private SharedPreferences recoveryPrefs;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        recoveryPrefs = getSharedPreferences(PREFS, MODE_PRIVATE);
        restoreHeaderArtwork();
    }

    @Override
    protected void onResume() {
        super.onResume();
        restoreHeaderArtwork();
        startAddressRecovery();
    }

    @Override
    protected void onPause() {
        stopAddressRecovery();
        super.onPause();
    }

    private void restoreHeaderArtwork() {
        View content = findViewById(android.R.id.content);
        if (content == null) return;

        TextView title = findExactText(content, "Turtle Transfer");
        if (title == null) title = findExactText(content, "CampTransfer");
        if (title == null || !(title.getParent() instanceof LinearLayout)) return;

        LinearLayout titleBlock = (LinearLayout) title.getParent();
        if (!(titleBlock.getParent() instanceof LinearLayout)) return;
        LinearLayout header = (LinearLayout) titleBlock.getParent();

        for (int i = 0; i < header.getChildCount(); i++) {
            Object tag = header.getChildAt(i).getTag();
            if ("turtle-transfer-header-art".equals(tag)) return;
        }

        ImageView logo = new ImageView(this);
        logo.setTag("turtle-transfer-header-art");
        logo.setImageResource(R.mipmap.turtle_transfer_launcher);
        logo.setScaleType(ImageView.ScaleType.CENTER_INSIDE);
        logo.setContentDescription("Turtle Transfer");

        LinearLayout.LayoutParams logoParams = new LinearLayout.LayoutParams(dpV32(48), dpV32(48));
        logoParams.rightMargin = dpV32(10);
        int titleIndex = header.indexOfChild(titleBlock);
        header.addView(logo, Math.max(0, titleIndex), logoParams);
    }

    private void startAddressRecovery() {
        stopAddressRecovery();
        recoveryPoller = Executors.newSingleThreadScheduledExecutor();
        recoveryPoller.scheduleWithFixedDelay(this::recoverSavedAddressIfNeeded, 4, 8, TimeUnit.SECONDS);
    }

    private void stopAddressRecovery() {
        if (recoveryPoller != null) {
            recoveryPoller.shutdownNow();
            recoveryPoller = null;
        }
    }

    private void recoverSavedAddressIfNeeded() {
        if (recoveryPrefs == null) return;
        String direct = recoveryPrefs.getString(PREF_HOST, "");
        if (direct == null || direct.trim().isEmpty()) return;
        direct = CampTransferClient.normalizeHost(direct);

        try {
            CampTransferClient.fetchStatus(direct);
            return;
        } catch (Exception ignored) {
        }

        try {
            CampTransferClient.DiscoveredPc discovered = CampTransferClient.discover();
            String discoveredHost = CampTransferClient.normalizeHost(discovered.host);
            if (discoveredHost.isEmpty() || discoveredHost.equals(direct)) return;

            recoveryPrefs.edit().putString(PREF_HOST, discoveredHost).apply();
            final String foundHost = discoveredHost;
            recoveryUi.post(() -> {
                updateHostEditor(foundHost);
                Toast.makeText(
                        this,
                        "Found Turtle Transfer at " + foundHost,
                        Toast.LENGTH_SHORT).show();
            });
        } catch (Exception ignored) {
            // The regular status UI continues to show Offline while waiting for the PC or firewall.
        }
    }

    private void updateHostEditor(String host) {
        View content = findViewById(android.R.id.content);
        EditText editor = findHostEditor(content);
        if (editor != null) editor.setText(host);
    }

    private TextView findExactText(View root, String wanted) {
        if (root instanceof TextView) {
            CharSequence text = ((TextView) root).getText();
            if (text != null && wanted.equals(text.toString())) return (TextView) root;
        }
        if (root instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) root;
            for (int i = 0; i < group.getChildCount(); i++) {
                TextView found = findExactText(group.getChildAt(i), wanted);
                if (found != null) return found;
            }
        }
        return null;
    }

    private EditText findHostEditor(View root) {
        if (root == null) return null;
        if (root instanceof EditText) {
            EditText edit = (EditText) root;
            CharSequence hint = edit.getHint();
            if (hint != null && hint.toString().contains("IP address or hostname")) return edit;
        }
        if (root instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) root;
            for (int i = 0; i < group.getChildCount(); i++) {
                EditText found = findHostEditor(group.getChildAt(i));
                if (found != null) return found;
            }
        }
        return null;
    }

    private int dpV32(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
