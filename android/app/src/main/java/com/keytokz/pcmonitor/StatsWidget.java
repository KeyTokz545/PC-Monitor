package com.keytokz.pcmonitor;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.os.SystemClock;
import android.view.View;
import android.widget.RemoteViews;

import org.json.JSONObject;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

/**
 * Widget de l'écran d'accueil : températures, ventilation et profil CPU, avec boutons.
 * Mise à jour toutes les minutes seulement quand le téléphone est allumé (alarme sans réveil),
 * donc rien ne tourne écran éteint. Le PC n'a rien de plus à faire : il répond déjà à l'appli.
 */
public class StatsWidget extends AppWidgetProvider {

    static final String ACTION_REFRESH = "com.keytokz.pcmonitor.WIDGET_REFRESH";
    static final String ACTION_MODE = "com.keytokz.pcmonitor.WIDGET_MODE";
    static final String ACTION_CPU = "com.keytokz.pcmonitor.WIDGET_CPU";

    static final String[] MODES = {"silent", "balanced", "turbo"};
    static final int[] MODE_IDS = {R.id.w_m_silent, R.id.w_m_balanced, R.id.w_m_turbo};
    static final int[] CPU_IDS = {R.id.w_c_silent, R.id.w_c_balanced, R.id.w_c_turbo};

    @Override
    public void onUpdate(Context ctx, AppWidgetManager mgr, int[] ids) {
        schedule(ctx);
        refreshAsync(ctx, null, null);
    }

    @Override
    public void onEnabled(Context ctx) {
        schedule(ctx);
    }

    @Override
    public void onDisabled(Context ctx) {
        AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
        if (am != null) am.cancel(broadcast(ctx, ACTION_REFRESH, null, 0));
    }

    @Override
    public void onReceive(Context ctx, Intent intent) {
        super.onReceive(ctx, intent);
        String a = intent.getAction();
        if (ACTION_REFRESH.equals(a)) refreshAsync(ctx, null, null);
        else if (ACTION_MODE.equals(a)) refreshAsync(ctx, "{\"action\":\"mode\",\"mode\":\"" + intent.getStringExtra("v") + "\"}", "Ventilation…");
        else if (ACTION_CPU.equals(a)) refreshAsync(ctx, "{\"action\":\"cpu_profile\",\"id\":\"" + intent.getStringExtra("v") + "\"}", "Profil CPU…");
    }

    /** Une fois par minute, sans réveiller le téléphone : rien quand l'écran est éteint. */
    static void schedule(Context ctx) {
        AlarmManager am = (AlarmManager) ctx.getSystemService(Context.ALARM_SERVICE);
        if (am == null) return;
        PendingIntent pi = broadcast(ctx, ACTION_REFRESH, null, 0);
        am.setInexactRepeating(AlarmManager.ELAPSED_REALTIME, SystemClock.elapsedRealtime() + 60_000, 60_000, pi);
    }

    static PendingIntent broadcast(Context ctx, String action, String value, int req) {
        Intent i = new Intent(ctx, StatsWidget.class).setAction(action);
        if (value != null) i.putExtra("v", value);
        return PendingIntent.getBroadcast(ctx, req, i, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }

    void refreshAsync(Context ctx, String actionJson, String busyText) {
        final PendingResult pending = goAsync();
        final Context app = ctx.getApplicationContext();
        if (busyText != null) push(app, base(app, null, busyText));
        new Thread(() -> {
            try {
                refresh(app, actionJson);
            } finally {
                pending.finish();
            }
        }).start();
    }

    static void refresh(Context ctx, String actionJson) {
        Net.Pc pc = Net.saved(ctx);
        if (pc == null) {
            push(ctx, base(ctx, null, "Ouvre l'appli pour connecter le PC"));
            return;
        }
        String host = Net.pickHost(ctx, pc);
        String root = "http://" + host + ":" + pc.port;
        String key = Net.code(ctx);
        if (actionJson != null) Net.post(root + "/api/action?k=" + key, actionJson);
        StringBuilder body = new StringBuilder();
        int code = Net.get(root + "/api/stats?k=" + key, body);
        JSONObject s = null;
        if (code == 200) {
            try {
                s = new JSONObject(body.toString());
            } catch (Exception ignored) {
            }
        }
        push(ctx, base(ctx, s, s == null ? (code == 401 ? "Code invalide : ouvre l'appli" : "PC injoignable") : null));
    }

    static RemoteViews base(Context ctx, JSONObject s, String status) {
        RemoteViews v = new RemoteViews(ctx.getPackageName(), R.layout.widget);
        Intent open = new Intent(ctx, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        v.setOnClickPendingIntent(R.id.w_temps, PendingIntent.getActivity(ctx, 1, open, PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE));
        v.setOnClickPendingIntent(R.id.w_refresh, broadcast(ctx, ACTION_REFRESH, null, 2));
        for (int i = 0; i < MODES.length; i++) {
            v.setOnClickPendingIntent(MODE_IDS[i], broadcast(ctx, ACTION_MODE, MODES[i], 10 + i));
            v.setOnClickPendingIntent(CPU_IDS[i], broadcast(ctx, ACTION_CPU, MODES[i], 20 + i));
        }
        String mode = null, cpu = null;
        if (s != null) {
            v.setTextViewText(R.id.w_pc, s.optString("pc", "PC"));
            JSONObject c = s.optJSONObject("cpu"), g = s.optJSONObject("gpu");
            v.setTextViewText(R.id.w_cpu, temp(c));
            v.setTextColor(R.id.w_cpu, color(c));
            v.setTextViewText(R.id.w_gpu, temp(g));
            v.setTextColor(R.id.w_gpu, color(g));
            JSONObject m = s.optJSONObject("mode");
            mode = m != null ? m.optString("current", null) : null;
            cpu = s.isNull("cpu_profile") ? null : s.optString("cpu_profile", null);
            v.setTextViewText(R.id.w_status, "à " + new SimpleDateFormat("HH:mm", Locale.FRANCE).format(new Date()));
            v.setInt(R.id.w_dot, "setBackgroundResource", R.drawable.widget_dot_on);
        } else {
            v.setTextViewText(R.id.w_cpu, "–");
            v.setTextViewText(R.id.w_gpu, "–");
            v.setTextViewText(R.id.w_status, status == null ? "" : status);
            v.setInt(R.id.w_dot, "setBackgroundResource", R.drawable.widget_dot_off);
        }
        if (status != null && s != null) v.setTextViewText(R.id.w_status, status);
        for (int i = 0; i < MODES.length; i++) {
            boolean on = MODES[i].equals(mode);
            v.setInt(MODE_IDS[i], "setBackgroundResource", on ? R.drawable.widget_btn_on : R.drawable.widget_btn);
            v.setTextColor(MODE_IDS[i], on ? 0xFFE4ECF2 : 0xFF7F93A6);
            boolean onC = MODES[i].equals(cpu);
            v.setInt(CPU_IDS[i], "setBackgroundResource", onC ? R.drawable.widget_btn_on : R.drawable.widget_btn);
            v.setTextColor(CPU_IDS[i], onC ? 0xFFE4ECF2 : 0xFF7F93A6);
        }
        v.setViewVisibility(R.id.w_cpu_row, s != null && cpu == null ? View.GONE : View.VISIBLE);
        return v;
    }

    static String temp(JSONObject o) {
        if (o == null || o.isNull("temp")) return "–";
        return Math.round(o.optDouble("temp")) + "°";
    }

    static int color(JSONObject o) {
        if (o == null || o.isNull("temp")) return 0xFFE4ECF2;
        double t = o.optDouble("temp");
        if (t >= 90) return 0xFFFF5A4E;
        if (t >= 80) return 0xFFFF8A4C;
        if (t >= 70) return 0xFFFFB547;
        return 0xFFE4ECF2;
    }

    static void push(Context ctx, RemoteViews v) {
        AppWidgetManager mgr = AppWidgetManager.getInstance(ctx);
        mgr.updateAppWidget(new ComponentName(ctx, StatsWidget.class), v);
    }
}
