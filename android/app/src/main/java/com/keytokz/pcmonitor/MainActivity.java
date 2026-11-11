package com.keytokz.pcmonitor;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.graphics.Bitmap;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import java.util.List;

/** Le tableau de bord du PC en plein écran, écran toujours allumé. */
public class MainActivity extends Activity {

    private WebView web;
    private LinearLayout overlay;
    private TextView overlayText;
    private TextView wakeButton;
    private Net.Pc pc;
    private volatile boolean recovering;

    @SuppressLint({"SetJavaScriptEnabled", "AddJavascriptInterface"})
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        pc = Net.saved(this);
        if (pc == null) {
            openSetup();
            finish();
            return;
        }
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Ui.INK);

        web = new WebView(this);
        web.setBackgroundColor(Ui.INK);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setUserAgentString(s.getUserAgentString() + " PCMonitorApp/2.3");
        web.addJavascriptInterface(new Bridge(), "AndroidApp");
        web.setWebViewClient(new WebViewClient() {
            @Override
            public void onPageStarted(WebView view, String url, Bitmap favicon) {
                hideOverlay();
            }

            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame()) showOverlay();
            }

            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                Uri u = request.getUrl();
                if (u != null && pc.host.equals(u.getHost())) return false;
                try {
                    startActivity(new Intent(Intent.ACTION_VIEW, u));
                } catch (Exception ignored) {
                }
                return true;
            }
        });
        root.addView(web, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        overlay = buildOverlay();
        overlay.setVisibility(View.GONE);
        root.addView(overlay, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        setContentView(root);
        immersive();
        load();
    }

    private void load() {
        web.loadUrl(Net.base(pc) + "/?k=" + Net.code(this) + "&app=android");
    }

    private LinearLayout buildOverlay() {
        LinearLayout o = new LinearLayout(this);
        o.setOrientation(LinearLayout.VERTICAL);
        o.setGravity(Gravity.CENTER);
        o.setBackgroundColor(Ui.INK);
        int pad = Ui.dp(this, 32);
        o.setPadding(pad, pad, pad, pad);
        o.setClickable(true);

        TextView title = Ui.text(this, "PC injoignable", 24, Ui.TEXT, true);
        title.setGravity(Gravity.CENTER);
        o.addView(title);
        overlayText = Ui.text(this, "", 15, Ui.MUTED, false);
        overlayText.setGravity(Gravity.CENTER);
        o.addView(overlayText, Ui.full(this, 10));

        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(Ui.dp(this, 300), LinearLayout.LayoutParams.WRAP_CONTENT);
        lp.topMargin = Ui.dp(this, 24);
        TextView retry = Ui.button(this, "Réessayer", true);
        retry.setOnClickListener(v -> load());
        o.addView(retry, lp);

        LinearLayout.LayoutParams lp2 = new LinearLayout.LayoutParams(Ui.dp(this, 300), LinearLayout.LayoutParams.WRAP_CONTENT);
        lp2.topMargin = Ui.dp(this, 10);
        wakeButton = Ui.button(this, "Allumer le PC à distance", false);
        wakeButton.setOnClickListener(v -> new Thread(() -> {
            boolean ok = Net.wake(pc.macs);
            runOnUiThread(() -> Toast.makeText(this,
                    ok ? "Signal de réveil envoyé. Le PC peut mettre une minute à démarrer." : "Adresse du PC inconnue",
                    Toast.LENGTH_LONG).show());
        }).start());
        o.addView(wakeButton, lp2);

        LinearLayout.LayoutParams lp3 = new LinearLayout.LayoutParams(Ui.dp(this, 300), LinearLayout.LayoutParams.WRAP_CONTENT);
        lp3.topMargin = Ui.dp(this, 10);
        TextView change = Ui.button(this, "Changer de PC", false);
        change.setOnClickListener(v -> openSetup());
        o.addView(change, lp3);
        return o;
    }

    private void showOverlay() {
        runOnUiThread(() -> {
            overlayText.setText("Vérifie que " + pc.name + " est allumé, que PC Monitor tourne, et que ce téléphone est sur le même Wi-Fi.");
            wakeButton.setVisibility(pc.macs == null || pc.macs.isEmpty() ? View.GONE : View.VISIBLE);
            overlay.setVisibility(View.VISIBLE);
        });
        recover();
    }

    private void hideOverlay() {
        runOnUiThread(() -> overlay.setVisibility(View.GONE));
    }

    /** L'adresse IP du PC a pu changer (box) : on le recherche par son nom et on recharge. */
    private void recover() {
        if (recovering) return;
        recovering = true;
        new Thread(() -> {
            try {
                List<Net.Pc> found = Net.discover(getApplicationContext(), 2500);
                for (Net.Pc f : found) {
                    if (f.name.equalsIgnoreCase(pc.name)) {
                        boolean moved = !f.host.equals(pc.host) || f.port != pc.port;
                        if (f.macs.isEmpty()) f.macs = pc.macs;
                        Net.save(this, f, Net.code(this));
                        pc = f;
                        runOnUiThread(this::load);
                        if (moved) runOnUiThread(() -> Toast.makeText(this, "Nouvelle adresse du PC : " + f.host, Toast.LENGTH_SHORT).show());
                        break;
                    }
                }
            } finally {
                recovering = false;
            }
        }).start();
    }

    private void openSetup() {
        startActivity(new Intent(this, SetupActivity.class));
    }

    @SuppressWarnings("deprecation")
    private void immersive() {
        if (Build.VERSION.SDK_INT >= 30) {
            getWindow().setDecorFitsSystemWindows(false);
            WindowInsetsController c = getWindow().getInsetsController();
            if (c != null) {
                c.hide(WindowInsets.Type.systemBars());
                c.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            }
        } else {
            getWindow().getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                            | View.SYSTEM_UI_FLAG_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                            | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                            | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN);
        }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) immersive();
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (web == null) return;
        Net.Pc saved = Net.saved(this);
        // retour de « Changer de PC » : on recharge avec le nouveau PC
        if (saved != null && (!saved.host.equals(pc.host) || saved.port != pc.port)) {
            pc = saved;
            load();
        } else if (overlay != null && overlay.getVisibility() == View.VISIBLE) {
            load();
        } else {
            web.onResume();
        }
    }

    @Override
    protected void onPause() {
        super.onPause();
        if (web != null) web.onPause();
    }

    @Override
    protected void onDestroy() {
        if (web != null) {
            web.destroy();
            web = null;
        }
        super.onDestroy();
    }

    /** Fonctions appelées par le tableau de bord (JavaScript). */
    public final class Bridge {
        @JavascriptInterface
        public void openSetup() {
            runOnUiThread(MainActivity.this::openSetup);
        }

        @JavascriptInterface
        @SuppressWarnings("deprecation")
        public void vibrate(int ms) {
            Vibrator v = (Vibrator) getSystemService(Context.VIBRATOR_SERVICE);
            if (v != null) v.vibrate(VibrationEffect.createOneShot(Math.max(10, Math.min(ms, 2000)), VibrationEffect.DEFAULT_AMPLITUDE));
        }

        @JavascriptInterface
        public boolean wake() {
            return Net.wake(pc.macs);
        }
    }
}
