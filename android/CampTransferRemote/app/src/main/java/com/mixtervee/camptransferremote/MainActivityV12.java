package com.mixtervee.camptransferremote;

import android.graphics.drawable.Drawable;
import android.os.Bundle;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewParent;
import android.widget.LinearLayout;
import android.widget.TextView;

import org.json.JSONObject;

import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

/**
 * Small v0.1.2 presentation layer on top of the proven v0.1.1 dashboard.
 * It adds the selected CampTransfer upload limit without changing the existing
 * monitoring, discovery, queue rendering, or notification logic.
 */
public class MainActivityV12 extends MainActivity {
    private TextView uploadLimitValue;
    private ScheduledExecutorService uploadLimitPoller;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        installUploadLimitStrip();
    }

    @Override
    protected void onResume() {
        super.onResume();
        startUploadLimitPolling();
    }

    @Override
    protected void onPause() {
        stopUploadLimitPolling();
        super.onPause();
    }

    private void installUploadLimitStrip() {
        TextView speedLabel = findTextView(getWindow().getDecorView(), "SPEED");
        if (speedLabel == null) return;

        ViewParent speedTileParent = speedLabel.getParent();
        if (!(speedTileParent instanceof LinearLayout speedTile)) return;

        ViewParent statsParent = speedTile.getParent();
        if (!(statsParent instanceof LinearLayout statsRow)) return;

        ViewParent cardParent = statsRow.getParent();
        if (!(cardParent instanceof LinearLayout card)) return;

        TextView speedValue = null;
        for (int i = 0; i < speedTile.getChildCount(); i++) {
            View child = speedTile.getChildAt(i);
            if (child instanceof TextView textView && child != speedLabel) {
                speedValue = textView;
                break;
            }
        }
        if (speedValue == null) return;

        LinearLayout strip = new LinearLayout(this);
        strip.setOrientation(LinearLayout.HORIZONTAL);
        strip.setGravity(Gravity.CENTER_VERTICAL);
        strip.setPadding(
                speedTile.getPaddingLeft(),
                speedTile.getPaddingTop(),
                speedTile.getPaddingRight(),
                speedTile.getPaddingBottom());

        Drawable sourceBackground = speedTile.getBackground();
        if (sourceBackground != null && sourceBackground.getConstantState() != null) {
            strip.setBackground(sourceBackground.getConstantState().newDrawable().mutate());
        }

        TextView label = cloneTextStyle(speedLabel, "UPLOAD LIMIT");
        strip.addView(label, new LinearLayout.LayoutParams(
                0,
                LinearLayout.LayoutParams.WRAP_CONTENT,
                1f));

        uploadLimitValue = cloneTextStyle(speedValue, "—");
        uploadLimitValue.setGravity(Gravity.END | Gravity.CENTER_VERTICAL);
        strip.addView(uploadLimitValue, new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.WRAP_CONTENT,
                LinearLayout.LayoutParams.WRAP_CONTENT));

        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        params.topMargin = dp(9);

        int statsIndex = card.indexOfChild(statsRow);
        card.addView(strip, Math.min(statsIndex + 1, card.getChildCount()), params);
    }

    private TextView cloneTextStyle(TextView source, String text) {
        TextView copy = new TextView(this);
        copy.setText(text);
        copy.setTextColor(source.getTextColors());
        copy.setTextSize(TypedValue.COMPLEX_UNIT_PX, source.getTextSize());
        copy.setTypeface(source.getTypeface());
        copy.setIncludeFontPadding(source.getIncludeFontPadding());
        return copy;
    }

    private TextView findTextView(View root, String exactText) {
        if (root instanceof TextView textView && exactText.contentEquals(textView.getText())) {
            return textView;
        }

        if (root instanceof ViewGroup group) {
            for (int i = 0; i < group.getChildCount(); i++) {
                TextView found = findTextView(group.getChildAt(i), exactText);
                if (found != null) return found;
            }
        }
        return null;
    }

    private void startUploadLimitPolling() {
        stopUploadLimitPolling();
        uploadLimitPoller = Executors.newSingleThreadScheduledExecutor();
        uploadLimitPoller.scheduleWithFixedDelay(this::pollUploadLimit, 0, 2, TimeUnit.SECONDS);
    }

    private void stopUploadLimitPolling() {
        if (uploadLimitPoller != null) {
            uploadLimitPoller.shutdownNow();
            uploadLimitPoller = null;
        }
    }

    private void pollUploadLimit() {
        String host = getSharedPreferences(PREFS, MODE_PRIVATE).getString(PREF_HOST, "");
        if (host == null || host.trim().isEmpty()) return;

        try {
            JSONObject status = CampTransferClient.fetchStatus(host);
            String limit = status.optString("uploadLimit", "—").trim();
            if (limit.isEmpty() || "Unknown".equalsIgnoreCase(limit)) limit = "—";
            final String display = limit;
            runOnUiThread(() -> {
                if (uploadLimitValue != null) uploadLimitValue.setText(display);
            });
        } catch (Exception ignored) {
            // Keep the last known value; the main dashboard owns connection errors.
        }
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
