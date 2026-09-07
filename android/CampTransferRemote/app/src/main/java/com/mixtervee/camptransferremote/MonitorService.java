package com.mixtervee.camptransferremote;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;
import android.os.IBinder;

import org.json.JSONObject;

import java.util.Locale;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

public class MonitorService extends Service {
    private static final String CHANNEL_ID = "camptransfer_monitor";
    private static final int FOREGROUND_ID = 45827;
    private static final int EVENT_ID = 45828;

    private ScheduledExecutorService executor;
    private SharedPreferences prefs;
    private String lastImportantStatus = "";

    @Override
    public void onCreate() {
        super.onCreate();
        prefs = getSharedPreferences(MainActivity.PREFS, MODE_PRIVATE);
        createNotificationChannel();
        startForeground(FOREGROUND_ID, buildNotification(
                "CampTransfer Remote",
                "Connecting to CampTransfer…",
                -1,
                true));
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (executor == null || executor.isShutdown()) {
            executor = Executors.newSingleThreadScheduledExecutor();
            executor.scheduleWithFixedDelay(this::poll, 0, 2, TimeUnit.SECONDS);
        }
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        if (executor != null) executor.shutdownNow();
        executor = null;
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    private void poll() {
        String host = prefs.getString(MainActivity.PREF_HOST, "");
        if (host == null || host.trim().isEmpty()) {
            stopSelf();
            return;
        }

        try {
            JSONObject status = CampTransferClient.fetchStatus(host);
            updateFromStatus(status);
        } catch (Exception ex) {
            Notification waiting = buildNotification(
                    "CampTransfer Remote",
                    "Waiting for CampTransfer • " + host,
                    -1,
                    true);
            getSystemService(NotificationManager.class).notify(FOREGROUND_ID, waiting);
        }
    }

    private void updateFromStatus(JSONObject status) {
        int filesLeft = status.optInt("filesLeft", 0);
        String pcName = status.optString("pcName", "CampTransfer");
        String state = status.optString("state", "Ready");
        String whenFinished = status.optString("whenFinished", "Do nothing");
        JSONObject active = status.optJSONObject("active");

        if (filesLeft == 0) {
            Notification complete = buildNotification(
                    "CampTransfer queue complete",
                    "Finished on " + pcName + ("Do nothing".equals(whenFinished) ? "" : " • " + whenFinished),
                    100,
                    false);
            NotificationManager manager = getSystemService(NotificationManager.class);
            manager.notify(EVENT_ID, complete);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N)
                stopForeground(STOP_FOREGROUND_REMOVE);
            else
                stopForeground(true);
            stopSelf();
            return;
        }

        String title;
        String detail;
        int progress = -1;

        if (active == null) {
            title = "CampTransfer • " + filesLeft + (filesLeft == 1 ? " file left" : " files left");
            detail = state;
        } else {
            String file = active.optString("fileName", "Transfer");
            String operation = active.optString("operation", "Copy");
            String speed = active.optString("speed", "");
            String eta = active.optString("eta", "");
            String itemStatus = active.optString("status", state);
            double percent = active.optDouble("progressPercent", 0d);
            progress = (int) Math.max(0, Math.min(100, Math.round(percent)));

            title = file;
            detail = String.format(Locale.getDefault(), "%.1f%% • %s", percent, operation);
            if (!speed.isEmpty()) detail += " • " + speed;
            if (!eta.isEmpty()) detail += " • ETA " + eta;
            if (!"Transferring".equals(itemStatus) && !"Resuming".equals(itemStatus))
                detail += " • " + itemStatus;

            boolean important = itemStatus.startsWith("Retrying") ||
                    itemStatus.startsWith("Source cleanup pending");
            if (important && !itemStatus.equals(lastImportantStatus)) {
                Notification event = buildNotification(file, itemStatus, progress, false);
                getSystemService(NotificationManager.class).notify(EVENT_ID, event);
            }
            lastImportantStatus = important ? itemStatus : "";
        }

        Notification notification = buildNotification(title, detail, progress, true);
        getSystemService(NotificationManager.class).notify(FOREGROUND_ID, notification);
    }

    private Notification buildNotification(String title, String text, int progress, boolean ongoing) {
        Intent openIntent = new Intent(this, MainActivity.class);
        openIntent.setFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent pendingIntent = PendingIntent.getActivity(
                this,
                0,
                openIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Notification.Builder builder = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);

        builder.setSmallIcon(com.mixtervee.camptransferremote.R.drawable.ic_transfer)
                .setContentTitle(title)
                .setContentText(text)
                .setContentIntent(pendingIntent)
                .setOnlyAlertOnce(true)
                .setOngoing(ongoing)
                .setCategory(Notification.CATEGORY_PROGRESS)
                .setShowWhen(false);

        if (progress >= 0)
            builder.setProgress(100, progress, false);
        else if (ongoing)
            builder.setProgress(0, 0, true);

        if (!ongoing) builder.setAutoCancel(true);
        return builder.build();
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return;
        NotificationChannel channel = new NotificationChannel(
                CHANNEL_ID,
                "CampTransfer progress",
                NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("Live CampTransfer progress and completion status");
        getSystemService(NotificationManager.class).createNotificationChannel(channel);
    }
}
